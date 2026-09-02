var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var api = app.MapGroup("/api");

api.MapGet("/books", () => Results.Ok());
api.MapPost("/orders", () => Results.Accepted());

var routeFromConfiguration = "/not-architecture";
api.MapGet(routeFromConfiguration, () => Results.Ok());
var unrelated = new SimilarApi();
unrelated.MapGet("/must-not-appear", () => { });

app.Run();

sealed class SimilarApi
{
    public void MapGet(string route, Action handler) { }
}
