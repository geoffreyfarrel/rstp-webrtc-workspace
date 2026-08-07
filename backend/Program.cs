using backend.Controllers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod();
    });
});
builder.Services.AddControllers();

var app = builder.Build();

// SIPSorcery does its own internal logging (ICE candidate gathering, STUN/TURN
// connectivity checks, etc.) via a static LogFactory that's separate from
// ASP.NET Core's DI logging unless explicitly wired up - without this, none
// of that detail ever reaches our console/log output.
SIPSorcery.LogFactory.Set(app.Services.GetRequiredService<ILoggerFactory>());

string relativePath = builder.Configuration["StreamSettings:FFmpegPath"] ?? "public/bin";
StreamController.Initialise(builder.Environment.ContentRootPath, relativePath);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// No UseHttpsRedirection(): the frontend talks to this backend over plain
// http://localhost:5014 (see VITE_BACKEND_URL). Redirecting to the https
// profile broke CORS preflights for /Stream/whep — browsers don't follow
// redirects for those, so the request just failed with no response.

app.UseCors();
app.MapControllers();

app.Run();