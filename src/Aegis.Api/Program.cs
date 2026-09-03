using Aegis.Collectors;

var builder = WebApplication.CreateBuilder(args);

// The API process hosts the collectors for now (one box, DESIGN-v2 architecture). Splitting them
// into a worker later is one line here and one project reference; the collectors do not care.
builder.Services.AddAegisCollectors(builder.Configuration);

var app = builder.Build();

app.MapGet("/", () => "AEGIS API");

app.Run();
