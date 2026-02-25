# HumbleRedeemer

---

[![Repobeats analytics image](https://repobeats.axiom.co/api/embed/4aa3ac833c7593826ac47ccfdc49c46ae27abb3d.svg "Repobeats analytics image")](https://github.com/JustArchiNET/ASF-PluginTemplate/pulse)

---

## Description

This plugin enables automatic login and session management for HumbleBundle.com within the ASF framework.

My motivation for this topic came after I read about Steam keys beeing replaced by Epic Games with keyless keys. So you can no longer share that ~~key~~ license after you tried to redeem it.

- public gift links shared outside family and friends -> account banned with everything
- expired keys
- sold out keys... WTF??? So HumbleBundle sells keys which they do not have...
- Choice is an Abo but the keys expire now too
- Region Lock
- non exhausted keys but trying to redeem says they are exhausted.

---

## Installation

### 1. Build the Plugin

```bash
dotnet publish HumbleRedeemer -c Release -o ASF/plugins/
```

### 2. Configure HumbleBundle Credentials

Add HumbleBundle settings directly to your bot's configuration file in `config/<BotName>.json`:

```json
{
  "HumbleBundleEnabled": true,
  "HumbleBundleUsername": "your_humblebundle_email@example.com",
  "HumbleBundlePassword": "your_humblebundle_password",
  "HumbleBundleTwoFactorCode": "",
  "HumbleBundleRedeemRetryIntervalMinutes": 60,
  "HumbleBundleIgnoreStoreLocation": false,
  "HumbleBundleAutoRetry": true,
  "HumbleBundleUseGiftLinkForOwned": false,
  "HumbleBundleRedeemOnlyWithExpiration": false,
  "HumbleBundleBlacklistedGameKeys": [],
  "HumbleBundleBlacklistedAppIds": [],
  "HumbleBundleRedeemButNotToSteamAppIds": [],
  "HumbleBundleSkipUnknownAppIds": false,
  "HumbleBundleIgnoreStoreLocationButRedeem": false
}
```

**Configuration Properties:**

- `HumbleBundleEnabled` - Set to `true` to enable HumbleBundle for this bot
- `HumbleBundleUsername` - Your HumbleBundle account email
- `HumbleBundlePassword` - Your HumbleBundle account password
- `HumbleBundleTwoFactorCode` - Optional 2FA secret, otherwise you will be asked for the 6 digit code
- `HumbleBundleRedeemRetryIntervalMinutes` - Interval in minutes for retrying failed redemptions (default: 60)
- `HumbleBundleIgnoreStoreLocation` - If `true`, ignore region restrictions when redeeming keys (default: false)
- `HumbleBundleAutoRetry` - If `true`, automatically retry redeeming failed keys periodically (default: true)
- `HumbleBundleUseGiftLinkForOwned` - If `true`, redeem games already in your library as gift links instead of regular keys (default: false)
- `HumbleBundleRedeemOnlyWithExpiration` - If `true`, only redeem keys that have an expiration date, skipping keys that never expire (default: false)
- `HumbleBundleBlacklistedGameKeys` - List of Humble Bundle game keys to skip during order fetching (replaces hardcoded list). Example: `["X", "Y"]` (default: [])
- `HumbleBundleBlacklistedAppIds` - List of Steam App IDs to never redeem. Example: `[730, 570]` (default: [])
- `HumbleBundleRedeemButNotToSteamAppIds` - List of Steam App IDs to reveal keys for but not send to Steam. Example: `[730, 570]` (default: [])
- `HumbleBundleSkipUnknownAppIds` - If `true`, skip redeeming keys that don't have a Steam App ID set (default: false)
- `HumbleBundleIgnoreStoreLocationButRedeem` - If `true`, reveal keys ignoring region restrictions but don't send them to Steam (default: false)

---

## Implementation Details

- Sample `MyAwesomePlugin` ASF plugin project with `ArchiSteamFarm` reference in git subtree.
- Project structure supporting `IGitHubPluginUpdates` ASF interface, allowing for convenient plugin updates.
- Seamless hook into the ASF build process, which simplifies the project structure, as you effectively inherit the default settings official ASF projects are built with. Of course, free to override.
- GitHub actions CI script, which verifies whether your project is possible to build. You can easily enhance it with unit tests when/if you'll have any.
- GitHub actions publish script, heavily inspired by ASF build process. Publish script allows you to `git tag` and `git push` selected tag, while CI will build, pack, create release on GitHub and upload the resulting artifacts, automatically.
- GitHub actions ASF reference update script, which by default runs every day and ensures that your git submodule is tracking latest ASF (stable) release. Please note that this is a reference update only, the actual commit your plugin is built against is developer's responsibility not covered by this action, as it requires actual testing and verification. Because of that, commit created through this workflow can't possibly create any kind of build regression, it's a helper for you to more easily track latest ASF stable release.
- Configuration file for **[Renovate](https://github.com/renovatebot/renovate)** bot, which you can optionally decide to use. Using renovate, apart from bumping your library dependencies, can also cover bumping ASF commit that your plugin is built against, which together with above workflow will ensure that you're effectively tracking latest ASF (stable) release.
- Code style that matches the one we use at ASF, feel free to modify it to suit you.
- Other misc files for integration with `git` and GitHub.

---

## Recommended steps

Here we list steps that are **not mandatory**, but worthy to consider after using this repo as a template. While we'd recommend to cover all of those, it's totally alright if you don't. We ordered those according to our recommended priority.

- If you want to use automatic plugin updates, ensure **[`RepositoryName`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/MyAwesomePlugin/MyAwesomePlugin.cs#L13)** property matches your target repo, this is covered by default in our **[`rename.sh`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/tools/rename.sh)** script. If you want to opt out of that feature, replace **[`IGitHubPluginUpdates`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/MyAwesomePlugin/MyAwesomePlugin.cs#L11)** interface back to its base `IPlugin` one, and remove **[`RepositoryName`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/MyAwesomePlugin/MyAwesomePlugin.cs#L13)** property instead.
- Choose license based on which you want to share your work. If you'd like to use the same one we do, so Apache 2.0, then you don't need to do anything as the plugin template comes with it. If you'd like to use different one, remove **[`LICENSE.txt`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/LICENSE.txt)** file and provide your own. If you've decided to use different license, it's probably also a good idea to update `PackageLicenseExpression` in **[`Directory.Build.props`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/Directory.Build.props#L16)**.
- Change this **[`README.md`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/README.md)** in any way you want to. You can check **[ASF's README](https://github.com/JustArchiNET/ArchiSteamFarm/blob/main/README.md)** for some inspiration. We recommend at least a short description of what your plugin can do. Updating `<Description>` in **[`Directory.Build.props`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/Directory.Build.props#L14)** also sounds like a good idea.
- Fill **[`SUPPORT.md`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/.github/SUPPORT.md)** file, so your users can learn where they can ask for help in regards to your plugin.
- Fill **[`SECURITY.md`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/.github/SECURITY.md)** file, so your users can learn where they should report critical security issues in regards to your plugin.
- If you want to use **[Renovate bot](https://github.com/renovatebot/renovate)** like we do, we recommend to modify the `:assignee()` block in our **[`renovate.json5`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/.github/renovate.json5#L5)** config file and putting your own GitHub username there. This will allow Renovate bot to assign failing PR to you so you can take a look at it. Everything else can stay as it is, unless you want to modify it of course.
- Provide your own **[`CODE_OF_CONDUCT.md`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/.github/CODE_OF_CONDUCT.md#enforcement)** if you'd like to. If you're fine with ours, you can simply replace `TODO@example.com` e-mail with your own.

---

### Compilation

Simply execute `dotnet build MyAwesomePlugin` and find your binaries in `MyAwesomePlugin/bin` folder, which you can drag to ASF's `plugins` folder. Keep in mind however that your plugin build created this way is based on existence of your .NET SDK and might not work on other machines or other SDK versions - for creating actual package with your plugin use `dotnet publish MyAwesomePlugin -c Release -o out` command instead, which will create a more general, packaged version in `out` directory. Likewise, use `-c Debug` if for some reason you'd like more general `Debug` build instead.

### Library references

Our plugin template uses centrally-managed packages. Simply add a `PackageVersion` reference below our `Import` clause in **[`Directory.Packages.props`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/Directory.Packages.props#L2)**. Afterwards add a `PackageReference` to your **[`MyAwesomePlugin.csproj`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/MyAwesomePlugin/MyAwesomePlugin.csproj#L6-L10)** as usual, but without specifying a version (which we've just specified in `Directory.Packages.props` instead).

Using centrally-managed NuGet packages is crucial in regards to integration with library versions used in the ASF submodule, especially the `System.Composition.AttributedModel` which your plugin should always have in the ASF matching version. This also means that you don't have to (and actually shouldn't) specify versions for all of the libraries that ASF defines on its own in **[`Directory.Packages.props`](https://github.com/JustArchiNET/ArchiSteamFarm/blob/main/Directory.Packages.props)** (that you conveniently inherit from).

### Need help?

Feel free to ask in one of our **[support channels](https://github.com/JustArchiNET/ArchiSteamFarm/blob/main/.github/SUPPORT.md)**, where we'll be happy to offer you a helpful hand 😎.
