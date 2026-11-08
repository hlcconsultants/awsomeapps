# Privacy

FastGet has no accounts, analytics, or servers of its own. Nothing is sent to the FastGet project.

## What the Chrome extension handles

When you start a download or choose a detected video, the extension passes these to the FastGet app on your computer:

- The file's address and the page it came from
- The tab title, used as the name for video files
- Your browser's user agent
- Your cookies for the file's website, so downloads that need a login work

To detect video, the extension reads the addresses and response headers of media requests in your tabs. The list is kept in browser session storage and cleared when the tab navigates away or closes. It may re-request a video playlist from the site to identify its quality variants.

## What the app does with it

The app sends the address, page, user agent, and cookies only to the website hosting the file, as your browser would.

Your download list and settings are stored in `%AppData%\FastGet` in your Windows profile. Cookies for unfinished downloads are stored there unencrypted, and are erased when the download completes or is removed. Anyone with access to your Windows account could read them.

## Removing data

Removing a download deletes its stored data. Uninstalling FastGet keeps `%AppData%\FastGet`, so delete that folder to remove everything.
