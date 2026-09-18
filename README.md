# LoupixDeck Twitch plugin

Twitch plugin for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck). Windows only.

## Commands

| Command | What it does |
|---|---|
| `Twitch.SendChatMessage(<text>)` | Sends `<text>` to your chat (max 500 characters) |
| `Twitch.CreateClip` | Creates a clip (only while live) |
| `Twitch.ViewerCount` | Shows your viewer count on a touch button, `offline` when not live |
| `Twitch.ClearChat` | Clears your chat |
| `Twitch.ToggleSlowChat` | Turns slow mode on or off |
| `Twitch.ToggleEmotesOnly` | Turns emote-only mode on or off |
| `Twitch.RunCommercial` | Runs an ad (only while live, Partners/Affiliates) |
| `Twitch.CreateStreamMarker` | Sets a stream marker (only while live, VODs enabled) |

A chat message cannot contain `)`. Commas are kept: `Twitch.SendChatMessage(hi,all)` sends `hi, all`.

On a touch button each command shows its result for 10 seconds at the bottom of the button: `Sent`, `Clipped`, `Cleared`, `Slow ON`/`Slow OFF`, `Emotes ON`/`Emotes OFF`, `Marked`, `Offline`, `Sign in` or `Failed`. Run Ad counts down the ad seconds, or shows `Cooldown` when pressed too early.

## Setup

1. At <https://dev.twitch.tv/console/apps>, register an application: redirect URL `http://localhost:3000`, client type **Confidential**. Copy the Client ID and create a Client Secret.
2. In LoupixDeck: **Plugins** > **Install from Zip…** > pick `twitch-<version>-windows.zip` from the releases page. Then select your device under **Enabling for** and turn the Twitch plugin on.
3. In the plugin settings enter Client ID and Client Secret, click **Save**, then **Sign in with Twitch** and approve with your streaming account.

Settings:

- **Redirect port**: default `3000`. If you change it, change the redirect URL of your Twitch application too.
- **Slow mode wait**: 3, 5, 10, 20, 30, 60 or 120 seconds, default 30.
- **Ad length**: 30, 60, 90, 120, 150 or 180 seconds, default 30.

Other numbers are rounded to the nearest allowed value.

## Storage

Client ID, Client Secret and the sign-in token are stored in the plugin's `settings.json`. The token is encrypted with Windows DPAPI, so only your Windows account on this PC can use it. **Sign out** revokes it at Twitch and deletes it.
