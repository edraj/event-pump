import 'package:dio/dio.dart';
import 'package:event_pump/event_pump.dart';
import 'package:flutter/material.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // S0–S4 run here; init completes fast, context patches late (SPEC §3/§5)
  await EventPump.init(const EventPumpConfig(
    endpoint: 'https://collect.example.com',
    appToken: 'app-token-here',
    debug: true,
  ));

  // Tier-2 X-Event pattern: the platform emits the event server-side when
  // the request carries the headers (SPEC §8)
  final dio = Dio(BaseOptions(baseUrl: 'https://api.example.com'))
    ..interceptors.add(EventPumpDioInterceptor());

  runApp(ExampleApp(dio: dio));
}

class ExampleApp extends StatelessWidget {
  const ExampleApp({super.key, required this.dio});

  final Dio dio;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'event_pump example',
      // OPTIONAL automatic screen() — off unless you add the observer
      navigatorObservers: [EventPumpRouteObserver()],
      home: Scaffold(
        appBar: AppBar(title: const Text('event_pump example')),
        body: Center(
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              ElevatedButton(
                onPressed: () =>
                    EventPump.instance.track('product_viewed', {'sku': 'A1'}),
                child: const Text('track product_viewed'),
              ),
              ElevatedButton(
                onPressed: () => EventPump.instance.setUser('user-123'),
                child: const Text('setUser (login)'),
              ),
              ElevatedButton(
                // an add-to-cart the app already makes; the header rides along
                onPressed: () => dio.post<void>(
                  '/cart/items',
                  data: {'sku': 'A1'},
                  options: Options(extra: {'epEvent': 'add_to_cart'}),
                ),
                child: const Text('add to cart (X-Event tier-2)'),
              ),
              ElevatedButton(
                // late partial update once the Adjust SDK yields the adid —
                // the designed path (SPEC §6)
                onPressed: () async {
                  final adid = await fakeAdjustGetAdid();
                  await EventPump.instance.identify({'adjust_adid': adid});
                },
                child: const Text('identify(adjust_adid) late'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Stand-in for `Adjust.getAdid()` — wire the real Adjust SDK here.
Future<String> fakeAdjustGetAdid() async => 'adid-from-adjust-sdk';
