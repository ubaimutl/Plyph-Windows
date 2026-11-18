# Compatibility and limitations

Plyph is packaged for x64 PCs running Windows 10 or 11. Selection and replacement depend on what each app exposes to Windows.

## When the toolbar doesn’t appear

Some editors, embedded web pages, and protected or elevated windows expose limited selection information. Plyph may fall back to the cursor when it cannot get the selection’s screen position.

- Check **Settings → Toolbar** for **Show floating selection toolbar** and **Excluded apps**.
- Try **Open Plyph Actions** from the tray or assign the **Open actions** shortcut under **Settings → Shortcuts & Capture**.
- Select a sentence in Notepad to check whether the issue is specific to another app.

## When Replace is unavailable

Plyph needs an editable target and a confirmed selection for reliable replacement. If the selection changed, the source window closed, or the text came from clipboard fallback, choose **Copy** and paste the result where you want it. Replacement uses plain text, so rich formatting may need to be reapplied.

## Provider limits and results

Available models and request limits depend on your provider and account. Cloud providers need a network connection. Local providers need a running model server. Review generated text before using it, especially when accuracy or wording matters.

[All Windows guides](index.md)
