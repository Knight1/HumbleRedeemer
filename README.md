# HumbleRedeemer

---

[![Repobeats analytics image](https://repobeats.axiom.co/api/embed/4aa3ac833c7593826ac47ccfdc49c46ae27abb3d.svg "Repobeats analytics image")](https://github.com/JustArchiNET/ASF-PluginTemplate/pulse)

---

## Description

This plugin enables automatic login, key redemption for Steam Keys for HumbleBundle.com for ArchiSteamFarm.

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
  "HumbleBundleRedeemBlizzardKeyless": false,
  "HumbleBundleRedeemOriginKeyless": false,
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
- `HumbleBundleSkipUnknownAppIds` - When `true`, TPKs without a known Steam App ID are skipped entirely. When `false` (default), they are still revealed on Humble but never forwarded to Steam - Mostly DLCs and old Bundles (default: false)
- `HumbleBundleIgnoreStoreLocationButRedeem` - If `true`, reveal keys ignoring region restrictions but don't send them to Steam (default: false)
- `HumbleBundleAutoPayMonthly` - If `true`, automatically pay for the current Humble Choice month if it hasn't been paid yet (default: false)
- `HumbleBundlePayMonthlyButNotReveal` - If `true` (requires `HumbleBundleAutoPayMonthly`), pay for the current month but do not reveal any keys (default: false)
- `HumbleBundlePayMonthlyRevealButNotToSteam` - If `true` (requires `HumbleBundleAutoPayMonthly`), pay and reveal keys for the current month but do not send them to Steam (default: false)
- `HumbleBundleClaimVaultGames` - If `true`, register all Humble Vault games to the account so they remain accessible after the subscription ends. Games are only claimed once and tracked in the bot cache (default: false)
- `HumbleBundleRedeemOnSteam` - If `true`, every revealed Steam key is immediately submitted to Steam via ASF's native `Bot.Actions.RedeemKey`. Gift-link reveals and keys flagged via the `…ButNotToSteam` options are never forwarded to Steam regardless of this setting. On the first run after enabling this, previously-revealed keys for games not yet in the Steam library will also be submitted. Permanent failures (already-owned, region-locked, bad code) are recorded in the bot cache so they are not retried (default: false)
- `HumbleBundleScheduleChoiceCheck` - If `true`, schedule an in-process timer that fires on the **first Tuesday of every month at 10:00 America/Los_Angeles** (Pacific Time — Humble Choice's monthly release time, DST-aware) and runs `AutoPayMonthly` (if enabled) plus the full reveal/redeem pipeline. Lets the bot pick up the new month's keys at release time without waiting for the next ASF restart or the periodic retry timer. The timer is rescheduled on every fire, so it survives indefinitely as long as the bot is running (default: false)
- `HumbleBundleRedeemEpicKeyless` - If `true`, claim `epic_keyless` games from both Humble Choice and regular orders. Humble auto-links the game to the connected Epic Games account. For Choice the plugin additionally requires `userOptions.has_epic_account_id` to be `true` (a wasted Choice slot can't be undone); regular orders just attempt the claim and log if Humble rejects it (default: false)
- `HumbleBundleRedeemGogKeyless` - If `true`, claim `gog_keyless` games from both Humble Choice and regular orders. Humble auto-links to the connected GOG account. For Choice the plugin additionally requires both `userOptions.gog_account_id` and `userOptions.gog_username` to be set (default: false)
- `HumbleBundleRedeemBlizzardKeyless` - If `true`, claim `blizzard_keyless` games (e.g. Diablo IV) from both Humble Choice and regular orders. Humble auto-links to the connected Battle.net account. For Choice the plugin additionally requires `userOptions.has_battlenet_link` to be `true` (default: false)
- `HumbleBundleRedeemOriginKeyless` - If `true`, claim `origin_keyless` games from both Humble Choice and regular orders. Humble auto-links to the connected EA / Origin account. For Choice the plugin additionally requires `userOptions.origin_is_linked` to be `true` (default: false)
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

## Fanatical

The plugin can also forward Steam keys you've already revealed on **[Fanatical](https://www.fanatical.com/)** to Steam. It runs alongside the Humble integration and can be enabled per-bot, with or without Humble.

### How it works

Fanatical guards every key reveal behind an emailed verification code, which the plugin **cannot** intercept. By default the plugin therefore does **not** reveal keys — you do that yourself in the browser. Once a key is revealed, Fanatical's API exposes it indefinitely on the order page; the plugin reads those revealed keys and submits them to Steam via ASF's native `Bot.Actions.RedeemKey`.

Some accounts/sessions don't actually require the emailed code. Set `FanaticalAttemptReveal: true` and the plugin will probe the reveal endpoint with one unrevealed Steam key:

- If Fanatical hands the key back directly (no code needed), it reveals every remaining unrevealed Steam key the same way and forwards them to Steam.
- If Fanatical demands an emailed code **and ASF is not headless** (an interactive console is attached) during the startup pass, the plugin prompts you on the console for the code Fanatical just emailed, exchanges it for a verification token (`atok`), and then reveals **all** keys with that token.
- If Fanatical demands a code but ASF is headless (or it's a background retry pass where nobody is watching the console), the plugin stops after that single probe — which itself triggers one email — and falls back to manual reveal. It won't ask again until ASF restarts.

Pipeline:

1. List all your Fanatical orders.
2. Fetch new orders + re-fetch known orders that still have un-revealed items (so newly revealed keys are picked up automatically).
3. For every item with `status: "revealed"` and `drm.steam: true`, forward the `key` to Steam.
4. Persist a per-key `SteamRedeemAttempted` flag so terminal Steam responses (success / already-owned / region-locked / bad code) aren't retried on every cycle.
5. Repeat on the configured retry interval to catch newly-revealed keys without restarting ASF.

The Steam activation rate limit (30 keys, then 1 every 3 minutes) is shared with the Humble integration — both pause and resume together.

### Authentication

Fanatical's login is reCAPTCHA-protected and cannot be driven headlessly. Instead, paste the auth values from your already-logged-in browser:

1. Log in to [fanatical.com](https://www.fanatical.com/) in your browser.
2. Open DevTools → **Application** → **Local Storage** → `https://www.fanatical.com`.
3. Copy the value of **`bsauth`** — it's a JSON object. Run `JSON.parse(localStorage.bsauth).token` in the console (or just extract the `token` field manually) and put that into `FanaticalAuthToken`. The token is an opaque string of the form `<userId>.<uuid>` (NOT a `Bearer …` JWT).
4. *(Optional but recommended)* Copy the value of **`bsanonymous`** — same trick: `JSON.parse(localStorage.bsanonymous).id` → `FanaticalAnonId`. Fanatical's API accepts requests without it, but the official browser flow always sends it, so providing it makes the plugin look identical to a real browser.
5. The plugin caches the values and refreshes the token automatically via Fanatical's `/api/user/refresh-auth`. You only need to re-paste them if the cached token is rejected (rare).

You can verify your token works with:

```bash
curl 'https://www.fanatical.com/api/user/refresh-auth' \
  -H 'authorization: <your token>' | jq .token
```

### Configuration

Add to your bot config:

```json
{
  "FanaticalEnabled": true,
  "FanaticalAuthToken": "UserID.UUIDv4",
  "FanaticalAnonId": "abc123…",
  "FanaticalRedeemOnSteam": true,
  "FanaticalAttemptReveal": false,
  "FanaticalSteamKeysOnly": true,
  "FanaticalRedeemRetryIntervalMinutes": 60,
  "FanaticalAutoRetry": true,
  "FanaticalBlacklistedOrderIds": [],
  "FanaticalSkipSteamForSlugs": [],
  "FanaticalProxy": ""
}
```

**Configuration Properties:**

- `FanaticalEnabled` — Set to `true` to enable the Fanatical integration for this bot. Independent of `HumbleBundleEnabled` (default: false)
- `FanaticalAuthToken` — Opaque token from `JSON.parse(localStorage.bsauth).token`. Required for first run; cached + auto-refreshed afterwards
- `FanaticalAnonId` — *Optional.* Anonymous-id from `JSON.parse(localStorage.bsanonymous).id`. Cached after first use; sent as the `anonid` header when present
- `FanaticalRedeemOnSteam` — If `true`, every revealed Steam key found in the orders is submitted to Steam via ASF. Items still pending email-reveal are listed in the log so you know what to manually reveal in the browser (default: false)
- `FanaticalAttemptReveal` — If `true`, the plugin tries to reveal keys via the API instead of waiting for you to do it in the browser. It probes one unrevealed Steam key; if Fanatical returns it without an emailed code, it reveals all remaining unrevealed Steam keys the same way. If Fanatical requires an emailed verification code and ASF has an attached console (not headless) on the startup pass, you'll be prompted on the console to enter the code from the email, after which all keys are revealed. If ASF is headless (or on a background retry pass), it stops after the single probe — which sends one email — and doesn't retry until ASF restarts. Revealed keys are still only forwarded to Steam when `FanaticalRedeemOnSteam` is `true` (default: false)
- `FanaticalSteamKeysOnly` — If `true` (default), only items with `drm.steam: true` are tracked. Set to `false` to also cache items for other DRM platforms (Origin, Epic, GOG, etc.) — useful purely for visibility (default: true)
- `FanaticalRedeemRetryIntervalMinutes` — Interval in minutes between retry passes. Each pass re-lists Fanatical orders so newly revealed keys are picked up, and re-attempts any Steam submissions that previously hit a transient failure or rate limit (default: 60)
- `FanaticalAutoRetry` — If `true`, the periodic retry timer is started after the initial pass. Disable to make Fanatical processing a one-shot per session (default: true)
- `FanaticalBlacklistedOrderIds` — Fanatical order IDs (24-char hex from the `/orders/{id}` URL) to skip during fetching. Use to permanently exclude problematic orders. Example: `["6983341422d048a55b9b668f"]` (default: [])
- `FanaticalSkipSteamForSlugs` — Game slugs (URL-friendly identifiers like `easy-delivery-co`) whose revealed keys should NOT be forwarded to Steam, even if they're Steam-DRM. Useful when you want the key cached for gifting/trading but not activated on this bot's account. Example: `["easy-delivery-co"]` (default: [])
- `FanaticalProxy` — Optional HTTP/SOCKS5 proxy URL for Fanatical requests. Independent of `HumbleBundleProxy` — each integration uses its own handler. Same format as the Humble proxy option

### Per-bot files

Fanatical state is stored next to the bot config as `HumbleRedeemer-Fanatical-<BotName>.cache`. The Humble cache (`HumbleRedeemer-<BotName>.cache`) is separate.

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
