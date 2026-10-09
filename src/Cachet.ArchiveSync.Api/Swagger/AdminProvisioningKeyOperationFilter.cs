using System.Linq;
using Cachet.ArchiveSync.Api.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Cachet.ArchiveSync.Api.Swagger;

/// <summary>
/// Post-review Swagger amendment (2026-09-23): declares the <c>X-Admin-Provisioning-Key</c>
/// header as a Swagger security requirement, scoped only to actions guarded by
/// <see cref="AdminProvisioningTokenFilter"/> (i.e. <c>UsersAdminController</c>'s four
/// endpoints). Swagger UI has no generic "add a custom header" input — without a declared
/// security scheme, "Try it out" sends no header at all and every call 401s regardless of
/// whether the App Pool config is correct. This filter makes the header appear as a Swagger
/// "Authorize" input for just these endpoints, without affecting any other controller.
/// </summary>
public sealed class AdminProvisioningKeyOperationFilter : IOperationFilter
{
    public const string SchemeId = "AdminProvisioningKey";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var hasAdminTokenFilter = context.MethodInfo.DeclaringType?
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: true)
            .OfType<ServiceFilterAttribute>()
            .Any(f => f.ServiceType == typeof(AdminProvisioningTokenFilter)) ?? false;

        if (!hasAdminTokenFilter)
        {
            return;
        }

        operation.Security.Add(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = SchemeId
                    }
                },
                Array.Empty<string>()
            }
        });
    }
}
