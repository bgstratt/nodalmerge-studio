// Entry point for the out/studio-views.js bundle: the extracted Studio Shell view modules.
// Loaded at the end of <body> (after window.__nmVscode exists and all panes are parsed).
// Views are added here one at a time as they migrate out of their panels' inline template
// strings; a view listed here must no longer emit an inline <script> from getFragment().

import { bootstrapShell } from './bootstrap.js';
import { runView } from './runtime.js';
import { init as insights } from './insights.js';
import { init as projectionComparison } from './projectionComparison.js';
import { init as modelAgentStudio } from './modelAgentStudio.js';
import { init as executionTimeline } from './executionTimeline.js';
import { init as decisionConvergence } from './decisionConvergence.js';
import { init as goalWorkspace } from './goalWorkspace.js';

bootstrapShell();

runView('shell-pane-goal-workspace', goalWorkspace);
runView('shell-pane-model-agent-studio', modelAgentStudio);
runView('shell-pane-execution-timeline', executionTimeline);
runView('shell-pane-decision-convergence', decisionConvergence);
runView('shell-pane-insights', insights);
runView('shell-pane-projection-comparison', projectionComparison);
