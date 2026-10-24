using Monica.Core.Modularity.Extensions;
using Monica.Reference.Api.Composition;

var builder = WebApplication.CreateBuilder(args);
builder.AddMonica(monica => monica.ConfigureReferenceApplication());

var app = builder.Build();
app.UseMonica();
app.MapReferenceOverview();
app.MapMonica();
app.Run();
