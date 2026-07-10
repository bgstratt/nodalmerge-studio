// The 2s poll ticks rebuild whole sections via innerHTML — that destroys any live text
// selection inside the rebuilt subtree, which made copying anything out of a polling view
// (goal text, agent conversation, rejection reasons, work unit ids) nearly impossible: the
// selection vanished on the next tick. Views call this before a poll-driven re-render and
// skip the tick while the user has an uncollapsed selection anchored inside the container
// about to be replaced. The selection collapses on the user's next click anyway, so the
// tick after that picks up fresh data — no stale-forever risk in practice.
export function hasSelectionWithin(container) {
  if (!container) { return false; }
  var sel = window.getSelection();
  if (!sel || sel.isCollapsed || sel.rangeCount === 0) { return false; }
  return !!(sel.anchorNode && container.contains(sel.anchorNode));
}
