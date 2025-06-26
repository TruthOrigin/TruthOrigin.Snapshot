(() => {
    const SCRIPT_VERSION = 1;
    const SNAPSHOT_QUERY_KEY = "truth-snapshot";
    const ELEMENT_TAG_NAME = "snapshot-ready";
    const MAX_WAIT_TIME_MS = 10_000;

    function startSnapshotCheck() {
        const query = new URL(location.href).searchParams;
        const snapshotRequested = query.has(SNAPSHOT_QUERY_KEY);

        if (!snapshotRequested) return;

        console.log(`[TruthOrigin v${SCRIPT_VERSION}] Snapshot script initialized`);
        console.log(`[TruthOrigin v${SCRIPT_VERSION}] Current URL:`, location.href);
        console.log(`[TruthOrigin v${SCRIPT_VERSION}] Snapshot param detected:`, snapshotRequested);

        console.log(`[TruthOrigin v${SCRIPT_VERSION}] Snapshot mode activated. Looking for element: <${ELEMENT_TAG_NAME}>`);

        waitForElement(ELEMENT_TAG_NAME, MAX_WAIT_TIME_MS)
            .then(() => {
                console.log(`[TruthOrigin v${SCRIPT_VERSION}] Custom element detected — capturing HTML...`);
                const rawHtml = document.documentElement.outerHTML;

                const snapshotSignal = document.querySelector(ELEMENT_TAG_NAME);
                if (snapshotSignal) {
                    snapshotSignal.setAttribute("data-truth-processed", "true");
                    snapshotSignal.style.display = "none";
                }


                const html = sanitizeAndTransformSnapshot(rawHtml);
                console.log(`[TruthOrigin v${SCRIPT_VERSION}] HTML captured. Length:`, html.length);
                postSnapshotData(html);
            })
            .catch((err) => {
                console.warn(`[TruthOrigin v${SCRIPT_VERSION}] Snapshot aborted. Reason:`, err);
            });
    }

    function waitForElement(tagName, timeout) {
        return new Promise((resolve, reject) => {
            console.log(`[TruthOrigin v${SCRIPT_VERSION}] Checking DOM for <${tagName}>...`);

            let resolved = false;

            const immediate = document.querySelector(`${tagName}:not([data-truth-processed="true"])`);
            if (immediate) {
                console.log(`[TruthOrigin v${SCRIPT_VERSION}] Element already present:`, immediate);
                resolve();
                return;
            }

            console.log(`[TruthOrigin v${SCRIPT_VERSION}] Element not found. Setting up MutationObserver and polling...`);

            const observer = new MutationObserver(() => {
                const el = document.querySelector(`${tagName}:not([data-truth-processed="true"])`);
                if (el) {
                    console.log(`[TruthOrigin v${SCRIPT_VERSION}] Element found via MutationObserver:`, el);
                    cleanup();
                    resolve();
                }
            });

            const poller = setInterval(() => {
                const el = document.querySelector(`${tagName}:not([data-truth-processed="true"])`);
                if (el) {
                    console.log(`[TruthOrigin v${SCRIPT_VERSION}] Element found via polling:`, el);
                    cleanup();
                    resolve();
                }
            }, 100);

            const timeoutId = setTimeout(() => {
                if (!resolved) {
                    console.warn(`[TruthOrigin v${SCRIPT_VERSION}] Timeout waiting for <${tagName}>.`);
                    cleanup();
                    reject("Element timeout");
                }
            }, timeout);

            function cleanup() {
                if (resolved) return;
                resolved = true;
                observer.disconnect();
                clearInterval(poller);
                clearTimeout(timeoutId);
            }

            observer.observe(document.documentElement, {
                childList: true,
                subtree: true
            });
        });
    }

    function hashString(str) {
        let hash = 0, i, chr;
        if (str.length === 0) return hash;
        for (i = 0; i < str.length; i++) {
            chr = str.charCodeAt(i);
            hash = ((hash << 5) - hash) + chr;
            hash |= 0; // Convert to 32bit integer
        }
        return hash.toString();
    }


    function sanitizeAndTransformSnapshot(html) {
        try {
            const parser = new DOMParser();
            const doc = parser.parseFromString(html, "text/html");
            const head = doc.head;
            const body = doc.body;

            // 1. Remove all JSON-LD script tags
            const jsonLdScripts = head.querySelectorAll('script[type="application/ld+json"]');
            jsonLdScripts.forEach(script => script.remove());

            // 2. Remove all title tags
            const titleTags = head.querySelectorAll("title");
            titleTags.forEach(title => title.remove());

            // 3. Remove all canonical link tags
            const canonicalLinks = head.querySelectorAll('link[rel="canonical"]');
            canonicalLinks.forEach(link => link.remove());

            // 4. Remove all meta tags except charset, viewport, http-equiv
            const metaTags = head.querySelectorAll("meta");
            metaTags.forEach(meta => {
                const name = meta.getAttribute("name")?.toLowerCase();
                const httpEquiv = meta.getAttribute("http-equiv");
                const charset = meta.getAttribute("charset");

                const isAllowed =
                    charset ||
                    httpEquiv ||
                    name === "viewport";

                if (!isAllowed) {
                    meta.remove();
                }
            });

            // 5. Inject snapshot version tag at the end of valid meta section
            const versionMeta = doc.createElement("meta");
            versionMeta.setAttribute("name", "truth:snapshot-version");
            versionMeta.setAttribute("content", "9"); // update this dynamically if needed
            head.insertBefore(versionMeta, head.querySelector("base") || head.firstChild);

            // 6. Inject snapshot-ready content wrapped in comments
            const snapshotTag = doc.querySelector("truth-snapshot-ready");
            if (snapshotTag) {
                const content = snapshotTag.innerHTML.trim();
                if (content.length > 0) {
                    head.appendChild(doc.createTextNode("\n"));
                    head.appendChild(doc.createComment(" Truth-Snapshot "));
                    head.appendChild(doc.createTextNode("\n"));

                    const wrapper = doc.createElement("div");
                    wrapper.innerHTML = content;

                    Array.from(wrapper.childNodes).forEach(child => {
                        head.appendChild(child);
                    });

                    head.appendChild(doc.createTextNode("\n"));
                    head.appendChild(doc.createComment(" Snapshot-Done "));
                    head.appendChild(doc.createTextNode("\n"));
                }
                snapshotTag.remove();
            }

            // 7. Inject TruthOrigin snapshot validator at end of body
            const validatorScript = doc.createElement("script");
            validatorScript.setAttribute("id", "truthorigin-snapshot");
            validatorScript.setAttribute("data-version", "9"); // <-- match snapshot version here
            validatorScript.innerHTML = `
(() => {
  console.log("[TruthOrigin] Snapshot validator activated.");
  const localScript = document.getElementById("truthorigin-snapshot");
  const snapshotVersion = localScript?.getAttribute("data-version")?.trim();
  const controller = new AbortController();
  setTimeout(() => controller.abort(), 10 * 60 * 1000);

  fetch("/index.html", {
    signal: controller.signal,
    cache: "no-cache"
  })
    .then(response => {
      if (!response.ok) throw new Error("Failed to fetch root index.html");
      return response.text();
    })
    .then(html => {
      const tempDom = document.createElement("html");
      tempDom.innerHTML = html;
      const rootScript = tempDom.querySelector('#truthorigin-snapshot');
      if (!rootScript) {
        console.warn("[TruthOrigin] Root index.html missing config script — skipping validation.");
        return;
      }

      const rootVersion = rootScript.getAttribute("data-version")?.trim();
      const forceOriginAttr = rootScript.getAttribute("data-force-origin")?.trim();
      const forceOrigin = forceOriginAttr === "true";

      if (forceOrigin) {
        const route = encodeURIComponent(location.pathname);
        console.log("[TruthOrigin] forceOrigin=true — redirecting to origin.");
        location.replace(\`/?route=\${route}\`);
        return;
      }

      if (!rootVersion) {
        console.warn("[TruthOrigin] Root version missing — skipping version check.");
        return;
      }

      if (!snapshotVersion) {
        console.warn("[TruthOrigin] No snapshot version found — skipping validation.");
        return;
      }

      if (snapshotVersion !== rootVersion) {
        const route = encodeURIComponent(location.pathname);
        console.log(\`[TruthOrigin] Version mismatch — snapshot=\${snapshotVersion}, root=\${rootVersion}. Redirecting.\`);
        location.replace(\`/?route=\${route}\`);
      } else {
        console.log(\`[TruthOrigin] Snapshot is current (v\${snapshotVersion}).\`);
      }
    })
    .catch(err => {
      if (err.name === "AbortError") {
        console.warn("[TruthOrigin] Fetch timeout — skipping root version check.");
      } else {
        console.warn("[TruthOrigin] Snapshot validator error — continuing anyway.", err);
      }
    });
})();
        `.trim();

            body.appendChild(validatorScript);

            return "<!DOCTYPE html>\n" + doc.documentElement.outerHTML;
        } catch (e) {
            console.error("Failed to sanitize snapshot:", e);
            return html;
        }
    }

    function postSnapshotData(html) {
        try {
            const origin = "*";
            console.log(`[TruthOrigin v${SCRIPT_VERSION}] Sending snapshot via postMessage...`);
            window.parent.postMessage({
                type: "truth:snapshot",
                html: html
            }, origin);
            console.log(`[TruthOrigin v${SCRIPT_VERSION}] postMessage sent successfully.`);
        } catch (e) {
            console.error(`[TruthOrigin v${SCRIPT_VERSION}] postMessage failed:`, e);
        }
    }

    // Listen for snapshot re-trigger
    window.addEventListener("message", (event) => {
        if (event?.data?.type === "truth:navigate") {
            console.log(`[TruthOrigin v${SCRIPT_VERSION}] Received navigate request. Rerunning snapshot check.`);
            setTimeout(startSnapshotCheck, 50);
        }
    });

    // Handle internal navigation routing
    window.addEventListener("message", (event) => {
        if (!event?.data || event.data.type !== "truth:navigate") return;

        const target = event.data.targetPath;
        if (typeof target === "string" &&
            window.location.pathname + window.location.search !== target) {
            console.log(`[TruthOrigin v${SCRIPT_VERSION}] Navigating to route:`, target);
            history.pushState({}, "", target);
            window.dispatchEvent(new PopStateEvent("popstate"));
        }
    });

    startSnapshotCheck();

    const debugComment = document.createComment(`truth-snapshot-script-version: ${SCRIPT_VERSION}`);
    document.documentElement.appendChild(debugComment);
})();

(() => {
    const SPA_ROUTE_QUERY_KEY = "route";

    const routeParam = new URL(location.href).searchParams.get(SPA_ROUTE_QUERY_KEY);
    if (routeParam) {
        const target = decodeURIComponent(routeParam);

        // Clean up URL for user (remove ?route=... but preserve the path)
        const cleanUrl = target + location.hash;
        window.history.replaceState(null, "", cleanUrl);

        // Delay routing slightly to give framework time to hook in if needed
        const doSoftRedirect = () => {
            console.log(`[TruthOrigin] Soft routing to preserved target route: ${target}`);
            // Trigger Blazor/SPA routing via popstate
            window.history.pushState({}, "", target);
            window.dispatchEvent(new PopStateEvent("popstate"));
        };

        if (document.readyState === "complete") {
            setTimeout(doSoftRedirect, 100); // wait a tick just in case
        } else {
            window.addEventListener("load", () => setTimeout(doSoftRedirect, 100));
        }
    }
})();