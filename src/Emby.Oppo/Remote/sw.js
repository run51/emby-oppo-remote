// Minimal service worker of the OPPO web app: it makes the page "installable" as an app
// (Chrome requires one). It caches nothing: every request goes straight to the network.
self.addEventListener('install', function () { self.skipWaiting(); });
self.addEventListener('activate', function (event) { event.waitUntil(self.clients.claim()); });
self.addEventListener('fetch', function (event) { event.respondWith(fetch(event.request)); });
