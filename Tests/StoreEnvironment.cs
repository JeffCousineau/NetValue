using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

internal sealed class StoreEnvironment(string path) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "NetValue.Checks";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = path;
    public string WebRootPath { get; set; } = path;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
