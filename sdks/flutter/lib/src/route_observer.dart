import 'package:flutter/widgets.dart';

import 'client.dart';

/// Optional automatic screen() tracking — OFF by default; opt in by adding it
/// to MaterialApp.navigatorObservers:
///
///   navigatorObservers: [EventPumpRouteObserver()]
class EventPumpRouteObserver extends RouteObserver<PageRoute<dynamic>> {
  EventPumpRouteObserver([EventPumpClient? client]) : _client = client;

  final EventPumpClient? _client;

  void _trackScreen(Route<dynamic>? route) {
    final name = route?.settings.name;
    if (route is PageRoute && name != null && name.isNotEmpty) {
      (_client ?? EventPump.instance).screen(name);
    }
  }

  @override
  void didPush(Route<dynamic> route, Route<dynamic>? previousRoute) {
    super.didPush(route, previousRoute);
    _trackScreen(route);
  }

  @override
  void didReplace({Route<dynamic>? newRoute, Route<dynamic>? oldRoute}) {
    super.didReplace(newRoute: newRoute, oldRoute: oldRoute);
    _trackScreen(newRoute);
  }

  @override
  void didPop(Route<dynamic> route, Route<dynamic>? previousRoute) {
    super.didPop(route, previousRoute);
    _trackScreen(previousRoute);
  }
}
