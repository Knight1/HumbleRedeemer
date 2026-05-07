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
- now they are shortening the expiry date AFTER purchase

Request your Data, including all revealed keys via (https://dsar.humblebundle.com/), "Access My Information", confirm the E-Mail, wait for the Link to download the .zip File.

---

## Installation

### 1. Build the Plugin

```bash
git submodule update --init
dotnet publish HumbleRedeemer -c Release -o ASF/plugins/
```

### 2. Configure HumbleBundle Credentials

Add HumbleBundle settings directly to your bot's configuration file in `config/<BotName>.json`:

```json
{
  "HumbleBundleEnabled": true,
  "HumbleBundleUsername": "ign@humblebundle.com",
  "HumbleBundlePassword": "humblewasaweseome",
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
  "HumbleBundleIgnoreStoreLocationButRedeem": false,
  "HumbleBundleAutoPayMonthly": false,
  "HumbleBundlePayMonthlyButNotReveal": false,
  "HumbleBundlePayMonthlyRevealButNotToSteam": false,
  "HumbleBundleClaimVaultGames": false,
  "HumbleBundleRedeemOnSteam": false,
  "HumbleBundleScheduleChoiceCheck": false,
  "HumbleBundleRedeemEpicKeyless": false,
  "HumbleBundleRedeemGogKeyless": false,
  "HumbleBundleProxy": ""
}
```

**Configuration Properties:**

- `HumbleBundleEnabled` - Set to `true` to enable HumbleBundle for this bot
- `HumbleBundleUsername` - Your HumbleBundle account email
- `HumbleBundlePassword` - Your HumbleBundle account password. If omitted and ASF is not running in headless mode, the password will be prompted on the console at startup
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
- `HumbleBundleAutoPayMonthly` - If `true`, automatically pay for the current Humble Choice month if it hasn't been paid yet (default: false)
- `HumbleBundlePayMonthlyButNotReveal` - If `true` (requires `HumbleBundleAutoPayMonthly`), pay for the current month but do not reveal any keys (default: false)
- `HumbleBundlePayMonthlyRevealButNotToSteam` - If `true` (requires `HumbleBundleAutoPayMonthly`), pay and reveal keys for the current month but do not send them to Steam (default: false)
- `HumbleBundleClaimVaultGames` - If `true`, register all Humble Vault games to the account so they remain accessible after the subscription ends. Games are only claimed once and tracked in the bot cache (default: false)
- `HumbleBundleScheduleChoiceCheck` - If `true`, schedule an in-process timer that fires on the **first Tuesday of every month at 10:00 America/Los_Angeles** (Pacific Time — Humble Choice's monthly release time, DST-aware) and runs `AutoPayMonthly` (if enabled) plus the full reveal/redeem pipeline. Lets the bot pick up the new month's keys at release time without waiting for the next ASF restart or the periodic retry timer. The timer is rescheduled on every fire, so it survives indefinitely as long as the bot is running (default: false)
- `HumbleBundleRedeemEpicKeyless` - If `true`, also claim Humble Choice games whose only key type is `epic_keyless`. These games do not produce a redeemable key string — selecting them on Humble auto-links the game to the Humble account's connected Epic Games account. The plugin only acts when the Choice page's `userOptions.has_epic_account_id` is `true` (i.e. an Epic account is actually linked); otherwise the request would fail (default: false)
- `HumbleBundleRedeemGogKeyless` - If `true`, also claim Humble Choice games whose only key type is `gog_keyless`. Same flow as Epic above — selecting on Humble auto-links to the connected GOG account. The plugin only acts when both `userOptions.gog_account_id` and `userOptions.gog_username` are set on the Choice page (default: false)
- `HumbleBundleProxy` - Optional HTTP/SOCKS5 proxy URL for all Humble Bundle requests. Required when running on a VPS or cloud server whose IP is blocked by Cloudflare (see [Proxy Support](#proxy-support) below)

### Proxy Support

Cloudflare blocks POST requests from datacenter/hosting IP ranges to Humble Bundle's key redemption endpoints (`/humbler/redeemkey`, `/humbler/choosecontent`). If you are running ASF on a VPS or cloud server, you will need a residential proxy to bypass this.

When a Cloudflare IP/ASN block is detected, the plugin logs an error and **skips all further Humble Bundle requests** until restarted with a proxy configured.

Add the proxy to your bot configuration:

```json
{
  "HumbleBundleProxy": "socks5://127.0.0.1:1080"
}
```

**Supported proxy formats:**

| Format | Example |
|--------|---------|
| SOCKS5 | `socks5://127.0.0.1:1080` |
| SOCKS5 with auth | `socks5://user:pass@proxy.ign.com:1080` |
| HTTP | `http://proxy.ign.com:8080` |
| HTTP with auth | `http://user:pass@proxy.ign.com:8080` |

### key_types
- "generic"
- "steam"
- "origin"
- "epic_keyless"
- "gog_keyless"
- "blizzard"
- "uplay"
- "uplay_keyless"
- "external_key"
- "squareenix"
- "arenanet"
- "gog"
- "nintendo_direct"
- "origin_keyless"
- "desura"

---

## Recommended steps

Here we list steps that are **not mandatory**, but worthy to consider after using this repo as a template. While we'd recommend to cover all of those, it's totally alright if you don't. We ordered those according to our recommended priority.


- Fill **[`SUPPORT.md`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/.github/SUPPORT.md)** file, so your users can learn where they can ask for help in regards to your plugin.
- Fill **[`SECURITY.md`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/.github/SECURITY.md)** file, so your users can learn where they should report critical security issues in regards to your plugin.
- If you want to use **[Renovate bot](https://github.com/renovatebot/renovate)** like we do, we recommend to modify the `:assignee()` block in our **[`renovate.json5`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/.github/renovate.json5#L5)** config file and putting your own GitHub username there. This will allow Renovate bot to assign failing PR to you so you can take a look at it. Everything else can stay as it is, unless you want to modify it of course.
- Provide your own **[`CODE_OF_CONDUCT.md`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/.github/CODE_OF_CONDUCT.md#enforcement)** if you'd like to. If you're fine with ours, you can simply replace `TODO@example.com` e-mail with your own.

---

### Library references

Our plugin template uses centrally-managed packages. Simply add a `PackageVersion` reference below our `Import` clause in **[`Directory.Packages.props`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/Directory.Packages.props#L2)**. Afterwards add a `PackageReference` to your **[`MyAwesomePlugin.csproj`](https://github.com/JustArchiNET/ASF-PluginTemplate/blob/main/MyAwesomePlugin/MyAwesomePlugin.csproj#L6-L10)** as usual, but without specifying a version (which we've just specified in `Directory.Packages.props` instead).

Using centrally-managed NuGet packages is crucial in regards to integration with library versions used in the ASF submodule, especially the `System.Composition.AttributedModel` which your plugin should always have in the ASF matching version. This also means that you don't have to (and actually shouldn't) specify versions for all of the libraries that ASF defines on its own in **[`Directory.Packages.props`](https://github.com/JustArchiNET/ArchiSteamFarm/blob/main/Directory.Packages.props)** (that you conveniently inherit from).

### Need help?

Feel free to ask in one of our **[support channels](https://github.com/JustArchiNET/ArchiSteamFarm/blob/main/.github/SUPPORT.md)**, where we'll be happy to offer you a helpful hand 😎.
