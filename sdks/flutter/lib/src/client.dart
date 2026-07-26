import 'dart:async';
import 'dart:io';
import 'dart:ui' show AppLifecycleState, PlatformDispatcher;

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:device_info_plus/device_info_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter/widgets.dart'
    show WidgetsBinding, WidgetsBindingObserver, WidgetsFlutterBinding;
import 'package:package_info_plus/package_info_plus.dart';
import 'package:path_provider/path_provider.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:uuid/uuid.dart';

import 'allowlist.dart';
import 'config.dart';
import 'engagement.dart';
import 'queue.dart';

const String sdkName = 'event-pump-flutter';
const String sdkVersion = '0.1.0';

const _sessionWindowMs = 30 * 60 * 1000;
const _flushAt = 20;
const _flushIntervalMs = 30 * 1000;
const _giveUpMs = 24 * 60 * 60 * 1000;
const _backoffMs = [5000, 30000, 120000];

/// Pluggable persistence (SharedPreferences in production, memory in tests).
abstract class KeyValueStore {
  String? getString(String key);
  void setString(String key, String value);
}

/// Pluggable HTTP transport (dio in production, fakes in tests).
abstract class Transport {
  /// POSTs JSON; returns true on 2xx.
  Future<bool> post(String path, Map<String, Object?> body);
}

/// Core SDK logic — constructed with injectable dependencies so behavior is
/// fully unit-testable (fake_async), per PLAN §2.4. Production wiring lives
/// in [EventPump].
class EventPumpClient {
  EventPumpClient({
    required EventPumpConfig config,
    required KeyValueStore store,
    required Transport transport,
    required EventFileQueue queue,
    DateTime Function()? now,
    Future<Map<String, Object?>> Function()? contextCollector,
    Stream<void>? connectivityRegained,
  })  : _config = config,
        _store = store,
        _transport = transport,
        _queue = queue,
        _now = now ?? DateTime.now,
        _collectContext = contextCollector ?? (() async => <String, Object?>{});

  final EventPumpConfig _config;
  final KeyValueStore _store;
  final Transport _transport;
  final EventFileQueue _queue;
  final DateTime Function() _now;
  final Future<Map<String, Object?>> Function() _collectContext;
  final _uuid = const Uuid();
  final _stopwatch = EngagementStopwatch();

  late String _anonymousId;
  late String _firstSeenAt;
  String _sessionKey = '';
  int _sessionNumber = 0;
  String? _userId;
  String? _lastScreen;
  Map<String, Object?> _context = {};
  bool _saveData = false;

  bool _initialized = false;
  bool _gateOpen = false; // S4: nothing leaves before S3 completes (SPEC §3)
  bool _sending = false;
  int _failures = 0;
  int _nextAttemptAtMs = 0;
  Timer? _flushTimer;
  StreamSubscription<void>? _connectivitySub;

  int get _nowMs => _now().millisecondsSinceEpoch;

  /// S0–S4 (SPEC §3). Completes fast; registration and context collection are
  /// asynchronous and never block the caller.
  void init({Stream<void>? connectivityRegained}) {
    if (_initialized) return;
    _initialized = true;
    final nowMs = _nowMs;

    // S0: device identity — persisted random UUID, dies on uninstall,
    // no hardware ids ever (SPEC §2)
    final storedAid = _store.getString('ep_aid');
    _anonymousId = storedAid ?? _uuid.v4();
    if (storedAid == null) _store.setString('ep_aid', _anonymousId);
    _firstSeenAt = _store.getString('ep_first_seen_at') ??
        DateTime.fromMillisecondsSinceEpoch(nowMs, isUtc: true).toIso8601String();
    _store.setString('ep_first_seen_at', _firstSeenAt);
    _sessionNumber = int.tryParse(_store.getString('ep_session_number') ?? '') ?? 0;

    // S1: resume the persisted session within 30 minutes, else rotate
    // (approved decision: session_key persists across quick restarts)
    final storedKey = _store.getString('ep_session_key');
    final lastActive = int.tryParse(_store.getString('ep_last_active_at') ?? '');
    if (storedKey != null && lastActive != null && nowMs - lastActive <= _sessionWindowMs) {
      _sessionKey = storedKey;
    } else {
      _rotate(nowMs);
    }

    _stopwatch.start(nowMs);

    // S2 kicks off async; S3 registers; S4 opens the gate on completion
    _register(patchContextWhenReady: true);

    _flushTimer = Timer.periodic(const Duration(milliseconds: _flushIntervalMs), (_) => flush());
    _connectivitySub = connectivityRegained?.listen((_) => flush());
  }

  void _rotate(int nowMs) {
    _sessionKey = _uuid.v7();
    _sessionNumber += 1;
    _store.setString('ep_session_key', _sessionKey);
    _store.setString('ep_session_number', '$_sessionNumber');
    _touch(nowMs);
  }

  void _touch(int nowMs) => _store.setString('ep_last_active_at', '$nowMs');

  void _register({bool patchContextWhenReady = false}) {
    _gateOpen = false;
    final body = <String, Object?>{
      'session_key': _sessionKey,
      'anonymous_id': _anonymousId,
      'session_number': _sessionNumber,
      'first_seen_at': _firstSeenAt,
      if (_userId != null) 'user_id': _userId,
      'handles': {
        // anonymous_id IS the device id; never mint a separate one (SPEC §6)
        'amplitude_device_id': _anonymousId,
        'ga4_client_id': _anonymousId,
      },
      'context': _context,
    };
    unawaited(_transport.post('/v1/identity', body).then((_) {
      _gateOpen = true;
      flush();
    }));
    if (patchContextWhenReady) {
      // late context (post-first-frame view metrics, device_info) patches via
      // a partial upsert — init never blocks on platform channels (SPEC §5)
      unawaited(_collectContext().then((context) {
        if (context.isEmpty) return;
        _context = {..._context, ...context};
        _saveData = _context['save_data'] == true;
        unawaited(_transport.post('/v1/identity', {
          'session_key': _sessionKey,
          'anonymous_id': _anonymousId,
          'context': context,
        }));
      }));
    }
  }

  /// SPEC v1.1: `properties` is a **named** parameter — callers must write
  /// `ep.track('name', properties: {...})`. This is a breaking change from
  /// v1.0's positional form; it prevents future confusion with the
  /// person-level `attributes` on [setUserAttributes] (SPEC §6.1).
  void track(String eventName, {Map<String, Object?>? properties}) {
    if (!_initialized) return;
    final nowMs = _nowMs;
    _queue.append({
      'event_id': _uuid.v4(),
      'event_name': eventName,
      'occurred_at':
          DateTime.fromMillisecondsSinceEpoch(nowMs, isUtc: true).toIso8601String(),
      'anonymous_id': _anonymousId,
      'session_key': _sessionKey,
      if (_userId != null) 'user_id': _userId,
      if (properties != null) 'properties': properties,
      'context': {
        if (_lastScreen != null) 'screen': {'name': _lastScreen},
        'engagement_time_msec': _stopwatch.take(nowMs),
        'session_number': _sessionNumber,
        'sdk': {'name': sdkName, 'version': sdkVersion},
      },
    }, nowMs);
    _touch(nowMs);
    if (_queue.length >= _flushAt) flush();
  }

  /// See [track] — `properties` is a named parameter in v1.1.
  void screen(String screenName, {Map<String, Object?>? properties}) {
    _lastScreen = screenName;
    track('screen_view', properties: properties);
  }

  /// Login only — NEVER rotates anonymous_id (SPEC §3).
  void setUser(String userId) {
    _userId = userId;
    if (_initialized) _register();
  }

  /// Logout: drop user_id, rotate session_key only (SPEC §3).
  void clearUser() {
    _userId = null;
    if (!_initialized) return;
    _rotate(_nowMs);
    _register();
  }

  /// Thin error reporting: posts directly to /v1/errors, bypassing the queue
  /// and the S4 gate — it must work even when the SDK machinery is broken.
  /// To capture uncaught Flutter errors (opt-in), wire it yourself:
  ///   FlutterError.onError = (d) => EventPump.instance.reportError(d.exception, d.stack);
  void reportError(Object error, [StackTrace? stack]) {
    if (!_initialized) return;
    unawaited(_transport.post('/v1/errors', {
      'kind': error.runtimeType.toString(),
      'message': error.toString(),
      'stack': stack?.toString() ?? '',
      'anonymous_id': _anonymousId,
      'session_key': _sessionKey,
      'sdk': {'name': sdkName, 'version': sdkVersion},
    }));
  }

  /// Partial late handle updates, e.g. adjust_adid once Adjust yields it (SPEC §6).
  Future<void> identify(Map<String, Object?> handles) async {
    if (!_initialized) return;
    await _transport.post('/v1/identity', {
      'session_key': _sessionKey,
      'anonymous_id': _anonymousId,
      'handles': handles,
    });
  }

  /// Person-scoped user attributes (SPEC §6.1). Partial upsert — pass only
  /// the keys you want to change; pass `null` to clear a stored value.
  /// Requires a prior [setUser] call: without a user_id, this is a no-op
  /// in release and a `debugPrint` in debug (attributes attach to a person,
  /// not a session). Unknown keys are dropped locally against the shipped
  /// six-name allowlist; the server enforces the same set independently.
  Future<void> setUserAttributes(Map<String, Object?> attributes) async {
    if (!_initialized) return;
    if (_userId == null) {
      _debug('setUserAttributes ignored: no user_id (call setUser() first)');
      return;
    }
    final filtered = <String, Object?>{};
    for (final entry in attributes.entries) {
      if (kUserAttributesAllowlist.contains(entry.key)) {
        filtered[entry.key] = entry.value;
      } else {
        _debug('setUserAttributes: dropped unknown key "${entry.key}"');
      }
    }
    if (filtered.isEmpty) return;
    await _transport.post('/v1/identity', {
      'session_key': _sessionKey,
      'anonymous_id': _anonymousId,
      'user_id': _userId,
      'attributes': filtered,
    });
  }

  /// Tier-2 X-Event pattern headers (SPEC §8).
  Map<String, String> eventHeaders([String? eventName]) {
    if (!_initialized) return const {};
    return {
      if (eventName != null) 'X-Event': eventName,
      'X-Session-Key': _sessionKey,
      'X-Anonymous-Id': _anonymousId,
    };
  }

  Future<void> flush() async {
    if (!_gateOpen || _sending) return;
    final nowMs = _nowMs;
    if (nowMs < _nextAttemptAtMs) return;

    final expired = _queue
        .peek(_queue.length)
        .where((envelope) => nowMs - envelope.firstAttemptAtMs > _giveUpMs)
        .toList();
    if (expired.isNotEmpty) {
      _queue.ack(expired); // 24h give-up (SPEC §7)
      _debug('gave up on ${expired.length} event(s) older than 24h');
    }

    final batch = _queue.peek(_saveData ? _flushAt ~/ 2 : _flushAt);
    if (batch.isEmpty) return;

    _sending = true;
    final ok = await _transport.post('/v1/events', {
      'events': batch.map((envelope) => envelope.event).toList(),
    });
    _sending = false;
    if (ok) {
      _queue.ack(batch);
      _failures = 0;
      _nextAttemptAtMs = 0;
      if (_queue.length > 0) unawaited(flush());
    } else {
      final delay = _backoffMs[_failures < _backoffMs.length ? _failures : _backoffMs.length - 1];
      _failures += 1;
      _nextAttemptAtMs = _nowMs + delay;
      _debug('flush failed; next attempt in ${delay}ms');
    }
  }

  /// WidgetsBindingObserver hook: resumed => rotation check (rerun S1–S4 when
  /// rotated); paused => flush + stopwatch pause (SPEC §3/§4).
  void handleLifecycle(AppLifecycleState state) {
    if (!_initialized) return;
    final nowMs = _nowMs;
    if (state == AppLifecycleState.resumed) {
      _stopwatch.start(nowMs);
      final lastActive = int.tryParse(_store.getString('ep_last_active_at') ?? '');
      if (lastActive == null || nowMs - lastActive > _sessionWindowMs) {
        _rotate(nowMs);
        _register();
      }
      unawaited(flush());
    } else if (state == AppLifecycleState.paused) {
      _stopwatch.pause(nowMs);
      _touch(nowMs);
      unawaited(flush());
    }
  }

  void dispose() {
    _flushTimer?.cancel();
    unawaited(_connectivitySub?.cancel());
  }

  void _debug(String message) {
    if (_config.debug) {
      // ignore: avoid_print
      print('[event_pump] $message');
    }
  }
}

/// Production facade: wires SharedPreferences, dio, path_provider, the
/// platform context collectors, connectivity flushes, and the lifecycle
/// observer. `init()` completes fast — no platform channel work blocks the
/// caller beyond SharedPreferences/queue-path resolution (SPEC §3).
class EventPump {
  EventPump._();

  static EventPumpClient? _instance;

  static EventPumpClient get instance =>
      _instance ?? (throw StateError('EventPump.init() has not been called'));

  static Future<EventPumpClient> init(EventPumpConfig config) async {
    if (_instance != null) return _instance!;
    WidgetsFlutterBinding.ensureInitialized();

    final prefs = await SharedPreferences.getInstance();
    final supportDir = await getApplicationSupportDirectory();
    final queueDir = Directory('${supportDir.path}/event_pump');
    queueDir.createSync(recursive: true);

    final client = EventPumpClient(
      config: config,
      store: _PrefsStore(prefs),
      transport: _DioTransport(config),
      queue: EventFileQueue(File('${queueDir.path}/queue.jsonl')),
      contextCollector: () => _collectDeviceContext(config),
    );
    client.init(
      connectivityRegained: Connectivity()
          .onConnectivityChanged
          .where((results) => results.any((r) => r != ConnectivityResult.none))
          .map((_) {}),
    );
    WidgetsBinding.instance.addObserver(_LifecycleForwarder(client));
    _instance = client;
    return client;
  }
}

class _PrefsStore implements KeyValueStore {
  _PrefsStore(this._prefs);

  final SharedPreferences _prefs;

  @override
  String? getString(String key) => _prefs.getString(key);

  @override
  void setString(String key, String value) {
    unawaited(_prefs.setString(key, value));
  }
}

class _DioTransport implements Transport {
  _DioTransport(EventPumpConfig config)
      : _dio = Dio(BaseOptions(
          baseUrl: config.endpoint,
          headers: {'Authorization': 'Bearer ${config.appToken}'},
          connectTimeout: const Duration(seconds: 10),
          receiveTimeout: const Duration(seconds: 10),
          validateStatus: (_) => true,
        ));

  final Dio _dio;

  @override
  Future<bool> post(String path, Map<String, Object?> body) async {
    try {
      final response = await _dio.post<void>(path, data: body);
      final status = response.statusCode ?? 0;
      return status >= 200 && status < 300;
    } on DioException {
      return false;
    }
  }
}

class _LifecycleForwarder with WidgetsBindingObserver {
  _LifecycleForwarder(this._client);

  final EventPumpClient _client;

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) =>
      _client.handleLifecycle(state);
}

/// SPEC §5 context table, Flutter column. Runs after the first frame settles;
/// failures degrade to a partial map — context must never break tracking.
Future<Map<String, Object?>> _collectDeviceContext(EventPumpConfig config) async {
  final context = <String, Object?>{};
  try {
    final dispatcher = PlatformDispatcher.instance;
    context['language'] = dispatcher.locale.toLanguageTag();
    context['languages'] =
        dispatcher.locales.take(5).map((l) => l.toLanguageTag()).toList();
    final view = dispatcher.views.isEmpty ? null : dispatcher.views.first;
    if (view != null) {
      final size = view.physicalSize;
      context['screen_resolution'] =
          '${size.width.round()}x${size.height.round()}';
      context['dpr'] = view.devicePixelRatio;
      context['viewport'] =
          '${(size.width / view.devicePixelRatio).round()}x${(size.height / view.devicePixelRatio).round()}';
    }
    final nowLocal = DateTime.now();
    context['timezone'] = nowLocal.timeZoneName;
  } catch (_) {/* partial context is fine */}

  try {
    final deviceInfo = DeviceInfoPlugin();
    if (Platform.isAndroid) {
      final android = await deviceInfo.androidInfo;
      context['os'] = 'Android';
      context['os_version'] = android.version.release;
      context['model'] = android.model;
      context['category'] = 'mobile';
    } else if (Platform.isIOS) {
      final ios = await deviceInfo.iosInfo;
      context['os'] = 'iOS';
      context['os_version'] = ios.systemVersion;
      context['model'] = ios.utsname.machine;
      context['category'] = 'mobile';
    }
  } catch (_) {}

  try {
    final packageInfo = await PackageInfo.fromPlatform();
    context['app_version'] = config.appVersion ?? packageInfo.version;
    context['build'] = config.build ?? packageInfo.buildNumber;
  } catch (_) {}

  try {
    final connectivity = await Connectivity().checkConnectivity();
    context['connection_type'] = connectivity
        .map((result) => result.name)
        .firstWhere((name) => name != 'none', orElse: () => 'none');
  } catch (_) {}

  return context;
}
