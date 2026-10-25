# Monica

<p align="center">
  <img src="logo.png" alt="Monica Logo" width="200" />
</p>

<p align="center">
  <a href="https://github.com/Tairitsua/Monica/actions/workflows/unit-tests.yml"><img src="https://github.com/Tairitsua/Monica/actions/workflows/unit-tests.yml/badge.svg" alt="Unit Tests"></a>
  <a href="https://www.nuget.org/packages?q=Monica"><img src="https://img.shields.io/nuget/v/Monica.Core.svg" alt="NuGet"></a>
  <a href="https://github.com/Tairitsua/Monica/blob/main/LICENSE.txt"><img src="https://img.shields.io/github/license/Tairitsua/Monica" alt="License"></a>
  <a href="https://monica.dpdns.org/"><img src="https://img.shields.io/badge/docs-online-brightgreen.svg" alt="Documentation"></a>
  <a href="https://deepwiki.com/Tairitsua/Monica"><img src="https://deepwiki.com/badge.svg" alt="Ask DeepWiki"></a>
</p>

<p align="center">
  <a href="README.md">English</a> | 简体中文
</p>

<p align="center">
  <strong>AI agent 能遵循的架构，人类能检查的系统。</strong>
</p>

Monica 是面向 Agent 驱动开发的模块化 .NET 基础设施库。应用可按需采用各个模块；开发者与编码 Agent 共用模块组合、DDD ProjectUnit 和运行时诊断的类型化语言，使生成的代码结构可预期，运行中的系统可检查。

> **候选版本**：Monica 1.0.0-rc.12 是用于验证和反馈的预发布版本。在 1.0.0 稳定版之前仍可能出现破坏性变更。

## 快速链接

- 文档站点：<https://monica.dpdns.org/>
- Monica.Docs 示例仓库：<https://github.com/Tairitsua/Monica.Docs>
- JobScheduler 使用知识：<https://monica.dpdns.org/zh-CN/skills/monica-infra-jobs>
- 更新日志：[CHANGELOG.md](CHANGELOG.md)

## 为什么是 Monica

- AI 可以很快产出代码，但如果没有统一规格，代码会在规模增长后变得脆弱且难以观测。
- Monica 把基础设施本身变成规格：`AddMonica(...)` 先记录一个宿主的完整模块图，验证依赖与循环，再按确定性阶段应用注册。
- 每个组合都属于具体宿主；同一进程中的多个宿主不共享模块注册表、选项或 ProjectUnit 目录。
- 仓库以 `skills/` 作为 Monica Agent Skills 的唯一源码；`.agents/` 与 `.claude/` 下的投影由 `python scripts/sync_agent_skills.py --write` 按需生成，属于本地 Agent 状态、不进入 git，生成器只接管受管 Monica 目录，外部 Skill 原样保留。

## 演示视频

这段一分钟演示覆盖 Monica 面向运维的几个核心界面：

- JobScheduler 运行概览、owner 定义状态、执行记录和手动触发。
- 作业启停与历史保留策略管理，无需为后台作业手写运维页。
- ModuleSystem 对运行中宿主的性能视图和依赖视图。
- 运行时配置查看和主题切换。


https://github.com/user-attachments/assets/250e1e5f-0a78-4b8b-b832-756d682a01bd

## JobScheduler 代码示例

```csharp
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Extensions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Annotations;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);

builder.AddMonica(monica =>
{
    monica.ConfigureApplication(options =>
    {
        options.AppName = "Orders";
        options.AppId = "orders";
    });

    monica.AddJobScheduler(options => options.ProjectName = "Orders")
        .UseSchedulerScope("local-dev")
        .UseInMemoryStore();

    monica.AddJobSchedulerUI();
});

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();

[JobConfig(
    JobName = "Heartbeat",
    Description = "每五分钟写一次心跳日志。",
    CronSchedule = "0 */5 * * * *")]
public sealed class HeartbeatJob(ILogger<HeartbeatJob> logger) : RecurringJob(logger)
{
    public override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Heartbeat job ran.");
        return Task.CompletedTask;
    }
}
```

使用持久化生产 store 时，worker 崩溃后执行租约会以至少一次语义恢复，fencing 会拒绝已过期租约继续修改状态。每个宿主按自己的 owner 身份同步、调度并执行本地发现的作业；同一 scope 内的副本通过持久化 CAS 协调。并发门以 scheduler scope、owner 和 `JobKey` 为身份。但 fencing 无法让任意业务副作用自动获得严格一次语义；作业仍应使用幂等键或事务型业务边界保护这些副作用。

- 定时作业和触发式作业共享同一套持久化执行模型。
- 每个宿主同时运行本地定义同步、调度器和 worker；持久化游标、并发门与执行租约共同保证副本之间的分布式正确性。
- JobScheduler UI 工作区提供运行时 Tab：展示当前宿主的生效配置、存储身份、本机调度面就绪状态，以及范围内每个所有者的持久化足迹（在线阈值 = 3 个快照同步间隔，契约随数据下发）。同时注册了 ServiceDiscovery 的宿主可再加 `monica.AddJobSchedulerServiceDiscoveryInsight()`（包 `Monica.JobScheduler.ServiceDiscovery`），为该 Tab 增加基于心跳的 Worker 实例视图；未接入时仅隐藏该表并显示接入提示，不影响其余内容。
- 需要浏览器运维界面时，在同一个 `AddMonica(...)` 回调中加入 `monica.AddJobSchedulerUI()`。UI 需要 ASP.NET Core Web 宿主来提供 Blazor 路由和静态资源。
- 最小可运行参考见 [`examples/JobSchedulerMinimal`](examples/JobSchedulerMinimal)，它演示了如何用最少 ASP.NET Core 宿主在 `/job-scheduler` 跑起 JobScheduler + JobScheduler UI。

## 宿主级配置

应用身份和模块系统默认值在同一个宿主组合边界中配置：

```csharp
builder.AddMonica(monica =>
{
    monica.ConfigureApplication(options =>
    {
        options.AppName = "My Application";
        options.AppId = "my-application";
    });

    monica.ConfigureModuleSystem(options =>
    {
        options.DefaultApiGroupName = "Core";
    });
});
```

回调返回后模块图即被封闭，保留下来的 `ModuleRegistration<,>` 句柄不能再修改组合。

## 仪表板、主题与国际化

Monica.UI 之上还提供多个运维型 Blazor UI：JobScheduler、Configuration、DependencyInjection、ProjectUnits、ModuleSystem、AI 等。UI 层共用同一套主题契约，支持多个可切换主题，并内置 `en-US` + `zh-CN` 本地化资源。

## 随仓库交付的 Agent Skills

Monica 自有 Skill 的唯一源码位于 `skills/`；`.claude/skills/` 与 `.agents/skills/` 中对应的受管 Monica 子目录是面向仓库内发现的生成投影（本地按需生成、不进入 git），不应直接编辑或提交。

九个 `monica-infra-*` Skill 是基础设施使用知识的唯一维护位置：[`persistence`](skills/monica-infra-persistence)、[`messaging`](skills/monica-infra-messaging)、[`jobs`](skills/monica-infra-jobs)、[`configuration`](skills/monica-infra-configuration)、[`hosting`](skills/monica-infra-hosting)、[`observability`](skills/monica-infra-observability)、[`ai`](skills/monica-infra-ai)、[`web`](skills/monica-infra-web) 和 [`ui`](skills/monica-infra-ui)。应用架构、框架开发、测试和 UI 实现分别由其他 Skill 负责。

[Monica.Docs](https://monica.dpdns.org/) 从不可变发布资产 `monica-knowledge.json` 及其校验值发布这些 Skill，并维护稳定的中英文编辑文档；本地网站开发可预览当前源码。

- 初始化、配置与诊断入口：`monica-guide`；上游问题分类与草稿准备：`monica-contribution`
- 框架入口：`monica-framework`、`monica-development`、`monica-architecture`、`monica-ui-development`、`monica-ui-design`、`monica-ui-audit`、`monica-docs-authoring`、`monica-requirement-design`、`monica-unit-testing`、`monica-ui-bridge-debug`
- 第三方生态入口：`monica-third-party-module-development`，用于创建、校验、测试、打包和发布 `<Publisher>.Monica.<Package>` 包；一个 NuGet 包可以包含多个内聚模块
- 基于 Monica 的应用系统入口：`monica-application`、`monica-application-microservice`、`monica-application-modular-monolith`、`monica-application-project-unit-development`
- 外部配套 Skill（例如 `inspect-dependency-source` 与 `playwright-cli`）由各 Agent 环境单独安装和管理，Monica 的投影与更新流程不会改写它们

第三方包不得使用官方 `Monica.*` 前缀与紫色 Logo。兼容标识、包命名和独立维护声明见 [BRANDING.md](BRANDING.md)，完整中英文规范见 [Monica.Docs](https://monica.dpdns.org/)。

## 包成熟度

| 层级 | 承诺 | 代表能力 |
|---|---|---|
| **Stable** | 1.0 支持的应用主路径 | Core、ProjectUnits、WebApi、Configuration、Repository、JobScheduler、OpenTelemetry、UI |
| **Integrations** | 围绕外部系统的版本化适配器 | EF Core、Kafka、Redis/StackExchange、Dapr、SignalR |
| **Labs** | 保持快速演进的实验能力 | AI/RAG/MCP、DataChannel、DevOps/Profiling、Office、Experimental |

Stable 不依赖 Labs，Integration 也始终是按需引入的 provider-specific 包；CI 会同时验证包分类完整性和 maturity tier 的单向依赖规则。

## 架构速览

### 模块模式

- `Module{Name} : MonicaModule<Module{Name}Option>`：宿主拥有的模块生命周期策略
- `Module{Name}Option : ModuleOptions<Module{Name}>`：启动时冻结的配置与默认值
- `Module{Name}BuilderExtensions`：返回宿主绑定 `ModuleRegistration<,>` 的 `monica.Add{Name}()` 入口
- `Module{Name}RegistrationExtensions`：在该注册句柄上补充 provider 与能力选择的可选扩展

模块通过 `Describe(ModuleDescriptor)` 声明硬依赖与可选顺序关系。模块可直接读取自己的最终 `Option`；读取其他模块配置时，只能沿这些已声明关系访问。业务类型工作通过 `DeclareTypeDiscovery(...)` 声明一次；Monica 会把所有非空结构化查询合并到一次类型全集扫描中，再按模块图顺序串行提交匹配结果。

### ProjectUnit 模式

用于约束 AI 能写什么的类型化 DDD 单元：`ApplicationService`、`RequestDto`、`DomainService`、`Entity`、`Repository`、`DomainEvent`、`DomainEventHandler`、`LocalEventHandler`、`Configuration`、`RecurringJob`、`TriggeredJob`。

每个被发现的单元还可以声明明确的 Agent 上下文和需求追踪关系：

```csharp
[ProjectUnitMetadata(
    "审核订单",
    Owner = "订单团队",
    Description = "审核满足条件的订单。",
    Tags = ["ordering", "approval"])]
[ProjectUnitRequirement("ORD-REQ-001")]
public sealed class CommandHandlerApproveOrder : ApplicationService<CommandApproveOrder>
{
    // ...
}
```

`Monica.Framework.UI` 的第一个 Tab 会展示当前宿主的项目单元状态，包括类型分布、依赖健康度、告警，以及彼此独立的元数据、描述、负责人和需求覆盖率。`/framework/units`、`/framework/units/dashboard` 与 `/framework/units/{key}` 提供相同的类型化目录数据，不会把反射对象暴露到 UI 或 API 边界。

ProjectUnit 的详细约定可以在 `monica-application-project-unit-development` 和 Monica.Docs 的概念页里继续查看。

### 模块诊断

宿主通过 `monica.AddModuleSystem()` 显式启用 Core 诊断。它注册的 `ModuleDiagnosticsFacade` 为组合结果、耗时、回调、启动工作、类型发现阶段、诊断发现和直接依赖拓扑提供不可变且带修订号的快照。程序集清单按需加载，模块配置诊断单独获取；可移植导出不会包含配置数据、程序集路径、堆栈或原始异常详情。`monica.AddModuleSystemUI()` 会自动包含 Core 诊断模块。

每个公开模块配置属性都会以清理后的类型名保留在目录中。普通有界值自动展示；`[ModuleOptionDiagnosticsSensitive]` 或宿主策略标记的敏感内容会按类型缩减为非敏感的存在状态、数量或受保护地址。`RevealSensitive` 只会揭示有界敏感标量，并且只能用于 Development 环境中的专用调试。模块系统工作台默认也只在 Development 开放；其他环境必须显式启用并提供宿主授权策略。

## 技术栈

- [.NET 10](https://github.com/dotnet/runtime)
- [ASP.NET Core](https://github.com/dotnet/aspnetcore)
- [Entity Framework Core](https://github.com/dotnet/efcore)
- [MudBlazor](https://github.com/MudBlazor/MudBlazor)
- [Mapster](https://github.com/MapsterMapper/Mapster)
- [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
- [Dapr](https://github.com/dapr/dapr)
- [Serilog](https://github.com/serilog/serilog)
- [FluentValidation](https://github.com/FluentValidation/FluentValidation)
- [Polly](https://github.com/App-vNext/Polly)

## 贡献

1. Fork 仓库。
2. 创建分支。
3. 提交修改。
4. 提交 Pull Request。

完整贡献和发布说明见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 鸣谢

Monica 的一小部分模块实现思路参考了 [ABP Framework](https://github.com/abpframework/abp)，该项目采用 LGPL-3.0 许可证。任何直接改编的代码都会保留其原始声明和许可证条款。Monica 是独立项目，与 ABP 没有关联关系。

## 许可证

MIT License。见 [LICENSE.txt](LICENSE.txt)。

## 联系方式

- Issues：<https://github.com/Tairitsua/Monica/issues>
- Discussions：<https://github.com/Tairitsua/Monica/discussions>
- 文档站点：<https://monica.dpdns.org/>
- 安全：[SECURITY.md](SECURITY.md)
