using NodalMerge.Studio.Host;

var app = StudioWebApplication.Build(args);
var configuredUrl = app.Configuration["Studio:Urls"] ?? "http://127.0.0.1:5080";
app.Run(configuredUrl);
