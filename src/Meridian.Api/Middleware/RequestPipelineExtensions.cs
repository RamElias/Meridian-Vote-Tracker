namespace Meridian.Api.Middleware;

public static class RequestPipelineExtensions
{
    public static WebApplication UseMeridianRequestHandling(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128 || correlationId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-'))) correlationId = Guid.NewGuid().ToString("N");
            context.TraceIdentifier = correlationId;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Correlation-ID"] = correlationId;
                return Task.CompletedTask;
            });
            using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
                await next();
        });
        app.UseExceptionHandler(errors => errors.Run(async context =>
        {
            var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            var badRequest = error is BadHttpRequestException or System.Text.Json.JsonException;
            await Results.Problem(title: badRequest ? "Invalid request" : "The request could not be completed.",
                detail: badRequest ? "Check the request fields and JSON format." : "An internal error occurred. Use the correlation ID when reporting this problem.",
                statusCode: badRequest ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier }).ExecuteAsync(context);
        }));
        return app;
    }
}
