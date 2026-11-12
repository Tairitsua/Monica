# Monica.UI - UI基础架构类库

## 概述

`Monica.UI` 是一个基于 MudBlazor 的 Razor 类库，为其他基础架构模块提供UI界面支持。该模块采用模块化设计，允许其他基础架构项目引用并注册自己的UI组件。

## 功能特性

- 基于 MudBlazor 的现代化UI框架
- 模块化组件注册机制
- 可重用的布局和组件
- 类库模式，无需独立的入口程序
- 完整的文档注释支持

## 模块注册

每个 UI 模块必须显式声明页面标题所属的资源类型，并使用稳定的分类 ID。分类文本只用于显示，不能作为分组标识。

```csharp
public sealed class ModuleSignalRUI : MonicaModule<ModuleSignalRUIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<SignalRResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(
            static option => option.ConfigureNavigation(registry =>
                registry.RegisterLocalizedPage<UISignalRDebugPage, SignalRResource>(
                    UISignalRDebugPage.PAGE_URL,
                    "Pages:SignalRDebug:Title",
                    Icons.Material.Filled.Settings,
                    BuiltInNavigationCategoryIds.Debug,
                    addToNav: true,
                    navOrder: 20)));
    }
}

public static ModuleRegistration<ModuleSignalRUI, ModuleSignalRUIOption> AddSignalRUI(
    this IMonicaBuilder builder,
    Action<ModuleSignalRUIOption>? action = null)
{
    return builder.AddModule<ModuleSignalRUI, ModuleSignalRUIOption>(action);
}
```

模块自有分类应先通过 `RegisterLocalizedCategory<TResource>()` 注册 publisher-qualified ID，再将返回的
`NavigationCategoryId` 传给页面。注册表在路由读取时冻结，之后的修改会被拒绝。

## 最佳实践

1. **组件命名**：使用清晰的命名约定，如 `{模块名}{功能}Component`
2. **组件隔离**：每个基础架构模块的组件应该放在独立的命名空间中
3. **样式管理**：使用 MudBlazor 的主题系统来保持一致的UI风格
4. **服务注入**：在组件中通过依赖注入获取所需的服务
5. **错误处理**：在组件中实现适当的错误处理和用户反馈

## 静态资源生命周期

### Razor 类库静态资源访问

宿主不需要手工调用 `UseStaticWebAssets()` 或 `UseStaticFiles()`。在 `AddMonica(...)` 中注册 `monica.AddUIShell()` 或任意依赖 Shell 的 Monica UI 模块，然后执行完整 Web 生命周期：

```csharp
builder.AddMonica(monica => monica.AddUIShell());

var app = builder.Build();
app.UseMonica();
app.MapMonica();
```

`ModuleUIHost` 会在非 Production 环境的 builder 阶段加载 Razor 类库静态资源，并在 `app.UseMonica()` 阶段通过 `MapStaticAssets()` 映射资源端点。宿主没有 `.razor` 文件时，须在宿主项目中设置 `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>`，以包含 Blazor 启动脚本。业务 UI 可直接使用 `AddUIHost()`，按需添加 Shell；完整配置见 [Application Hosting](../skills/monica-infra-ui/references/application-hosting.md)。

#### 静态资源访问路径
- **wwwroot文件夹中的资源**：`/_content/Monica.UI/[相对路径]`
- **JavaScript文件**：`/_content/Monica.UI/js/filename.js`
- **CSS文件**：`/_content/Monica.UI/css/filename.css`

#### 在Razor组件中引用JavaScript模块
```cs
// 正确的引用方式
jsModule = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "/_content/Monica.UI/js/module.js");

```

### 离线静态资源管理

为了支持离线使用，所有第三方JavaScript库都应该下载到本地：

1. **下载依赖库**：将第三方库文件保存到`wwwroot/lib/`目录
2. **按需加载**：在组件中按正确顺序加载依赖项
3. **版本管理**：在README中记录使用的库版本

## Bundled Third-Party Assets

- `Mermaid` `11.12.3` is bundled locally at `wwwroot/lib/mermaid/mermaid.min.js`.
- The upstream license file is stored at `wwwroot/lib/mermaid/LICENSE`.
- Mermaid rendering is loaded through `/_content/Monica.UI/js/mo-markdown-mermaid.js` so runtime usage stays offline-friendly.
- Diagram labels use SVG text, and the canvas fits the rendered content after insertion to avoid stale browser measurements and oversized blank space.
