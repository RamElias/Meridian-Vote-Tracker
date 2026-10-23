using Microsoft.AspNetCore.Mvc;

namespace Meridian.Api.Controllers;

public abstract class ApiControllerBase : ControllerBase
{
    protected ObjectResult ApiProblem(string? title = null, string? detail = null, int? statusCode = null,
        IDictionary<string, object?>? extensions = null)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode, title, detail: detail);
        problem.Extensions["correlationId"] = HttpContext.TraceIdentifier;
        if (extensions is not null)
            foreach (var extension in extensions) problem.Extensions[extension.Key] = extension.Value;
        return new ObjectResult(problem) { StatusCode = problem.Status };
    }
}
