// The one HTML-escaping helper for all Studio Shell views. Escapes the full set
// (&, <, >, ", ') so the result is safe in both text and attribute contexts —
// several views historically shipped their own partial copies (&<> only), which is
// exactly the class of inconsistency that caused escaping bugs.

/** @param {unknown} s @returns {string} */
export function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  });
}
