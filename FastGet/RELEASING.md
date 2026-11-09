# Publishing a release

These steps need your own accounts, so they can't be automated from this repo.

## 1. Publish the source

FastGet lives in the `FastGet` folder of the public `awsomeapps` repository. Enable private vulnerability reporting under the repository's security settings.

## 2. Publish a build

Push a version tag such as `fastget-v1.0.0`. The `fastget-` prefix keeps its releases separate from other apps in the repository. The Release workflow runs the tests, builds the installer and extension package, and attaches them to a GitHub release.

## 3. Publish the extension

1. Register at the [Chrome Web Store developer dashboard](https://chrome.google.com/webstore/devconsole). Registration has a one-time fee, reported as $5.
2. Build locally and upload `installer/output/FastGet-Extension-Store-<version>.zip`. It has the manifest `key` removed because the store rejects it on a first upload. Don't use the zip attached to the release, which keeps its `key` for manual installs.
3. Paste the text from `store/LISTING.md`, and link to `PRIVACY.md` as the privacy policy.
4. Copy the extension ID the store assigns, and add it to `ExtensionIds` in `src/FastGet.App/NativeHostRegistrar.cs`. Release a new build, or the store version can't reach the app.

## 4. Sign the installer

Without signing, Windows SmartScreen warns people who download the installer.

[SignPath Foundation](https://signpath.org/) offers free signing to eligible open-source projects. It needs a public repository and an OSI-approved license, which the GPL is. Check the current terms before applying. Approved projects use SignPath's GitHub integration, so the Release workflow will need updating with the details they issue.

## 5. Check the name

No trademark search here is definitive. Search [USPTO](https://tmsearch.uspto.gov/) and your own country's register before promoting the project. An unrelated MIT-licensed command-line tool called [fastget](https://github.com/pgollangi/fastget) already exists.
