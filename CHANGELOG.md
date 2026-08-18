# Changelog

All notable changes to this package are documented here.

## [2.3.0]

- Rebranded the public namespace, assemblies, package id, folder, and menu as `ccd775.AvatarPhysBoneConverter` / `Tools/Avatar`.
- Added the conversion comparison image to the top of the repository README.
- Added a tool-only `.unitypackage` and a recommended package that bundles official UniVRM 0.131.2 for projects without UniVRM.
- Documented official UniVRM source, version, license, and SHA-256 verification.

## [2.2.0]

- Added independent converted Prefab saving with dedicated VRM metadata and an integrated entry to the UniVRM VRM 1.0 exporter.
- Added UPM/VPM package metadata, assembly definitions, MIT license, notices, and release documentation.
- Moved generated avatar data outside the package by default.
- Converted only active/enabled source components and blocked explicit Collider links that UniVRM would omit.
- Normalized scaled Inside Collider proxies so UniVRM preserves their world-space containment radius.
- Hardened rebuild/removal against renamed generated Springs, Prefab Mode edits, and leftover enabled-state overrides.
- Preserved the 2.1.2 partial-Immobile fix: only fully immobile chains receive a VRM SpringBone Center.

## [2.1.2]

- Preserved world-space inertia for partial or per-joint PhysBone Immobile values.
- Added total converted dynamic-joint count to the analysis summary.

## [2.1.0]

- Rebuilt conversion around VRC's initialized PhysBone topology and per-joint curves.
- Added explicit Sphere, Capsule, Inside, and Plane collider conversion.
- Added ColliderGroup link preservation and VRM 1.0 angle-limit extensions.
- Added non-destructive rebuild/removal through a generated-data manifest.
