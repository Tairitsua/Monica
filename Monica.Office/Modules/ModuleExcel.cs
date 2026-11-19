using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Office.Excel.Abstractions;
using Monica.Office.Excel.Providers.EpPlus;
using Monica.Office.Excel.Providers.Npoi;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleExcelBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the Excel module
        /// </summary>
        public ModuleRegistration<ModuleExcel, ModuleExcelOption> AddExcel(
            Action<ModuleExcelOption>? action = null)
        {
            return builder.AddModule<ModuleExcel, ModuleExcelOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleExcel, ModuleExcelOption> registration)
    {
        public ModuleRegistration<ModuleExcel, ModuleExcelOption> UseNpoi()
        {
            return registration
                .ConfigureServices(context =>
                {
                    context.Services.AddSingleton<INpoiCellStyleSupport, NpoiCellStyleSupport>();
                    context.Services.AddSingleton<INpoiWorkbookSupport, NpoiWorkbookSupport>();
                    context.Services.AddSingleton<IExcelImporter, NpoiExcelImportProvider>();
                    context.Services.AddSingleton<IExcelExporter, NpoiExcelExportProvider>();
                })
                .SatisfyFeature(ModuleExcel.PROVIDER_FEATURE);
        }

        public ModuleRegistration<ModuleExcel, ModuleExcelOption> UseEpPlus()
        {
            return registration
                .ConfigureServices(context =>
                {
                    context.Services.AddSingleton<IEpPlusCellStyleSupport, EpPlusCellStyleSupport>();
                    context.Services.AddSingleton<IEpPlusWorkbookSupport, EpPlusWorkbookSupport>();
                    context.Services.AddSingleton<IExcelImporter, EpPlusExcelImportProvider>();
                    context.Services.AddSingleton<IExcelExporter, EpPlusExcelExportProvider>();
                })
                .SatisfyFeature(ModuleExcel.PROVIDER_FEATURE);
        }
    }
}

public class ModuleExcel : MonicaModule<ModuleExcelOption>
{
    internal const string PROVIDER_FEATURE = "excel-provider";

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleTaskProgress>();
        module.RequireFeature(PROVIDER_FEATURE);
    }
}

public class ModuleExcelOption : ModuleOptions<ModuleExcel>
{
}
