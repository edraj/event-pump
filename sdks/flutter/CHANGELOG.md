# Changelog

## 0.2.0 - 2026-11-01

### Changed

- **Licence: AGPL-3.0-only -> LGPL-3.0-only.** LGPLv3 is a set of additional
  permissions on top of GPLv3, so the package ships both texts: `LICENSE`
  (LGPLv3) and `COPYING` (GPLv3). Linking `event_pump` into an application no
  longer places that application under copyleft, which is the point of
  licensing the client SDK apart from the Event Pump server; the server, and
  the rest of this repository, remain AGPL-3.0-only. 0.1.0 stays under the
  terms it was distributed with; **0.2.0 is the first version carrying
  LGPL-3.0-only**.
- **MoEngage before login is on by default.**
  `EventPumpConfig.moengageDeviceCustomerId` defaults to `true`: registrations
  made while no user is set carry `moengage_customer_id: anonymousId`, so
  pre-login events, `first_visit` included, reach MoEngage under the device id.
  Every device becomes a registered MoEngage user; pass `false` to opt out.
- The SDK version reported in event context (`sdk.version`) advances to 0.2.0.

### Added

- `EventPumpClient.anonymousId` (`String?`, `null` before `init()`), for
  passing the same device id to another SDK, e.g. `MoEngage.setUniqueId`.

## 0.1.0

The first version. Changes between 0.1.0 and 0.2.0 that are not listed above
were not versioned separately; see the repository history under `sdks/flutter`.
