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

A chat message cannot contain `)`. Commas are kept: `Twitch.SendChatMessage(hi,all)` sends `hi, all`.

## Setup

1. At <https://dev.twitch.tv/console/apps>, register an application: redirect URL `http://localhost:3000`, client type **Confidential**. Copy the Client ID and create a Client Secret.
2. In LoupixDeck: **Plugins** > **Install from Zip…** > pick `twitch-<version>-windows.zip` from the releases page. Then select your device under **Enabling for** and turn the Twitch plugin on.
3. In the plugin settings enter Client ID and Client Secret, click **Save**, then **Sign in with Twitch** and approve with your streaming account.

Settings:

- **Redirect port**: default `3000`. If you change it, change the redirect URL of your Twitch application too.
- **Slow mode wait**: 3 to 120 seconds, default 30.

## Storage

Client ID, Client Secret and the sign-in token are stored in the plugin's `settings.json`. The token is encrypted with Windows DPAPI, so only your Windows account on this PC can use it. **Sign out** revokes it at Twitch and deletes it.
