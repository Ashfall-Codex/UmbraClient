using Dalamud.Interface;
using Microsoft.Extensions.DependencyInjection;

namespace UmbraSync.Modules;

public interface IUmbraModule
{
    ModuleId Id { get; }
    string TitleKey { get; }
    string DescriptionKey { get; }
    FontAwesomeIcon Icon { get; }
    void ConfigureServices(IServiceCollection services);
}
