// <copyright file="BasicTests.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Instrumentation.AspNetCore.Implementation;
using OpenTelemetry.Trace;
using Xunit;

namespace OpenTelemetry.Instrumentation.AspNetCore.Tests;

public class MyTest
    : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> factory;
    private TracerProvider tracerProvider = null;

    public MyTest(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("/api/values/{id}", new[] { "id" })]
    [InlineData("/api/values/1", new string[0])]
    public async Task RedactUrlParameters(string expectedUrl, string[] urlParametersToRedact)
    {
        var exportedItems = new List<Activity>();
        void ConfigureTestServices(IServiceCollection services)
        {
            this.tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.UrlParametersToRedact = urlParametersToRedact;
                })
                .AddInMemoryExporter(exportedItems)
                .Build();
        }

        // Arrange
        using (var client = this.factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(ConfigureTestServices);
                builder.ConfigureLogging(loggingBuilder => loggingBuilder.ClearProviders());
            })
            .CreateClient())
        {
            // Act
            using var response = await client.GetAsync("/api/values/1").ConfigureAwait(false);

            // Assert
            response.EnsureSuccessStatusCode(); // Status Code 200-299

            WaitForActivityExport(exportedItems, 1);
        }

        Assert.Single(exportedItems);
        var activity = exportedItems[0];

        ValidateAspNetCoreActivity(activity, expectedUrl);
    }

    public void Dispose()
    {
        this.tracerProvider?.Dispose();
    }

    private static void WaitForActivityExport(List<Activity> exportedItems, int count)
    {
        // We need to let End callback execute as it is executed AFTER response was returned.
        // In unit tests environment there may be a lot of parallel unit tests executed, so
        // giving some breezing room for the End callback to complete
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                Thread.Sleep(10);
                return exportedItems.Count >= count;
            },
            TimeSpan.FromSeconds(1)));
    }

    private static void ValidateAspNetCoreActivity(Activity activityToValidate, string expectedHttpPath)
    {
        Assert.Equal(ActivityKind.Server, activityToValidate.Kind);
#if NET7_0_OR_GREATER
        Assert.Equal(HttpInListener.AspNetCoreActivitySourceName, activityToValidate.Source.Name);
        Assert.Empty(activityToValidate.Source.Version);
#else
            Assert.Equal(HttpInListener.ActivitySourceName, activityToValidate.Source.Name);
            Assert.Equal(HttpInListener.Version.ToString(), activityToValidate.Source.Version);
#endif
        Assert.Equal(expectedHttpPath, activityToValidate.GetTagValue(SemanticConventions.AttributeHttpTarget) as string, true);
    }
}
