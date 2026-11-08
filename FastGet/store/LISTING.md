# Chrome Web Store listing

**Name:** FastGet Integration

**Category:** Workflow & Planning

**Short description:** Send downloads and streaming videos to the FastGet download manager for Windows.

**Detailed description:**

FastGet Integration connects Chrome to FastGet, a free and open-source download manager for Windows.

- Hands browser downloads to FastGet for multi-connection downloading and reliable resume
- Adds "Download with FastGet" to the right-click menu
- Detects video and audio on the page you're viewing

Requires the FastGet desktop app. DRM-protected streams and YouTube are not supported.

## Single purpose

Send files and media the user chooses from Chrome to the FastGet desktop download manager.

## Permission justifications

| Permission | Why it's needed |
|---|---|
| `downloads` | Detect a new browser download and hand it to the app. |
| `cookies` | Pass the site's cookies to the app so downloads that need a login work. |
| `contextMenus` | Add "Download with FastGet" to the right-click menu. |
| `nativeMessaging` | Communicate with the FastGet desktop app. |
| `webRequest` | Read media response headers to detect video and audio. Requests aren't changed or blocked. |
| `tabs` | Associate detected media with a tab and name videos from the tab title. |
| `storage` | Save the capture setting and each tab's detected media. |
| Host access to all sites | Downloads and media can come from any site. |

## Data disclosure

Handled locally: website content (media addresses), web history (the page a download came from), and authentication information (cookies for the file's website). Data goes only to the FastGet app on the user's computer and from there to the website hosting the file. It isn't sold or sent to the developer.

Remote code: none.

Privacy policy: link to `PRIVACY.md` in the public repository.
