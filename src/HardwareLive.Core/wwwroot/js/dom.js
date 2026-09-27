// Small DOM helpers that keep every render path CSP-safe: untrusted hardware/sensor names
// only ever reach the DOM via textContent/setAttribute (docs/SPEC.md step5), never innerHTML,
// and any per-element color/width comes from element.style.setProperty (CSP's
// `style-src 'self'` blocks the `style="..."` attribute, but CSSOM writes are allowed).

import { sanitizeDisplayText } from './logic.js';

/** Creates an element and applies only safe, structured properties -- never raw HTML. */
export function el(tag, options = {}) {
  const node = document.createElement(tag);
  if (options.className) {
    node.className = options.className;
  }

  if (options.text != null) {
    node.textContent = options.text;
  }

  if (options.attrs) {
    for (const [name, value] of Object.entries(options.attrs)) {
      if (value === false || value == null) {
        continue;
      }

      node.setAttribute(name, value === true ? '' : String(value));
    }
  }

  if (options.children) {
    for (const child of options.children) {
      if (child) {
        node.append(child);
      }
    }
  }

  return node;
}

/** Sets sanitized, display-safe text (strips C0 control characters first). */
export function setSanitizedText(node, value) {
  node.textContent = sanitizeDisplayText(value);
}

export function clear(node) {
  while (node.firstChild) {
    node.removeChild(node.firstChild);
  }
}

/** CSP-safe per-element styling: element.style.setProperty, never the style attribute. */
export function setVar(node, name, value) {
  node.style.setProperty(name, value);
}

export function announce(liveRegionNode, text) {
  liveRegionNode.textContent = '';
  // Force a DOM mutation even when the announced text repeats, so screen readers re-read it.
  window.requestAnimationFrame(() => {
    liveRegionNode.textContent = text;
  });
}
