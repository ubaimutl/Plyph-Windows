# Privacy and access

Plyph detects text selections while it runs in the Windows system tray. It uses Windows UI Automation and local selection signals to find highlighted text and position the toolbar. Text can be read locally before you choose an action.

## What goes to a provider

When you run an action, Plyph sends the request and any included text to the provider configured for that action. This can include saved action instructions, relevant conversation turns when you follow up, and personal preferences when enabled for the action. In **Ask**, clear **Include selected text** to send the request without that selection.

Cloud providers process requests under their own policies. When you use a model server running on your PC, the text request goes to that local server.

## What Plyph uses and stores

| Data or access | How Plyph uses it |
| --- | --- |
| Clipboard | Plyph can use a copy command to capture a selection. **Copy** and **Replace** put the result on the clipboard. Using existing clipboard text when nothing is selected is a separate setting and is off by default. |
| Settings and actions | Installed mode stores files in `%APPDATA%\Plyph`. Portable mode uses the `data` folder beside its launcher. |
| API keys and ChatGPT sign-in credentials | Installed mode uses Windows Credential Manager. Portable mode uses a Windows-encrypted local file. |
| Request diagnostics | Off by default. When enabled, Plyph keeps recent metadata such as provider, model, estimated token count, and HTTP status in memory. Diagnostic logs omit selected text and API keys. |

Use **Settings → Toolbar → Excluded apps** to suppress automatic toolbar detection in specific apps. Use **Exit Plyph** from the tray menu to stop selection monitoring.

[All Windows guides](index.md)
