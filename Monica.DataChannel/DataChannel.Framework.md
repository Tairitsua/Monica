# Monica.DataChannel 架构说明

> 成熟度：**Labs**。本文描述当前源码中的公开组合模型，不构成 Stable 1.0 兼容性承诺。

## 1. 定位

DataChannel 用一条双向 pipeline 连接应用内侧与外部通信端点，并让 transform / monitor middleware 在数据经过时完成转换、观测或统计。

框架的核心边界是“每个宿主拥有自己的通道状态”：

- setup、pipeline builder 与已实例化 channel 不跨宿主共享；
- `IDataChannelManager` 只能看到当前 DI 容器拥有的 channel；
- TCP client、listener、故障切换状态与 Dapr route claim 都绑定当前宿主；
- 宿主停止时释放 endpoint 资源。

## 2. 组件关系

```text
builder.AddMonica(...)
        │
        ▼
AddDataChannel().UseSetup<TSetup>()
        │
        ▼
IDataChannelSetup.Setup(IDataChannelRegistrar)
        │  channels.Add(id, configure, groupId)
        ▼
ChannelPipelineBuilder
        │  materialize once
        ▼
DataChannelRuntime (host singleton)
        ├── IDataChannelManager
        └── DataChannel
              └── ChannelPipeline
                    ├── InnerEndpoint
                    ├── Transform / monitor middleware
                    └── OuterEndpoint
```

### `IDataChannelSetup`

一个宿主只注册一个 setup 实现。setup 的职责是声明 pipeline，不应保存运行期 channel 状态。

```csharp
public sealed class DemoChannelSetup : IDataChannelSetup
{
    public void Setup(IDataChannelRegistrar channels)
    {
        channels.Add("demo", pipeline =>
            pipeline.SetOuterEndpoint(new DefaultEndpointOptions()));
    }
}
```

### `IDataChannelRegistrar`

`Add(...)` 接收三个信息：

| 参数 | 含义 |
|---|---|
| `id` | 当前宿主内唯一的 channel ID。 |
| `configure` | 配置 endpoint 与 middleware 的回调。 |
| `groupId` | 可选分组，用于 `IDataChannelManager.FetchGroup(...)`。 |

以下情况会立即失败：空 ID、重复 ID、缺少 outer endpoint，或在 materialize 完成后继续注册。

### `ChannelPipelineBuilder`

公开配置入口包括：

- `SetInnerEndpoint(...)`
- `SetOuterEndpoint(...)`
- `AddPipeMiddleware<TMiddleware>()`
- `AddPipeMiddleware(params IPipelineMiddleware[])`

outer endpoint 必填；inner endpoint 未设置时使用 `DefaultEndpointOptions`。泛型 middleware 由宿主 DI 创建，直接传入的实例由 setup 明确拥有。

### `IDataChannelManager` 与 `DataChannel`

运行期通过 `IDataChannelManager` 查询 materialized channel：

```csharp
var channel = manager.Fetch("demo");
var group = manager.FetchGroup("imports");
var all = manager.FetchAll();
```

`DataChannel.SendDataFromInnerAsync(...)` 把数据送往 outer endpoint；`SendDataFromOuterAsync(...)` 走相反方向。

## 3. 数据流

`ChannelDataContext.Source` 决定最终目标：

```text
Source = Inner
Inner application → middleware chain → OuterEndpoint

Source = Outer
External provider → middleware chain → InnerEndpoint
```

transform middleware 按注册顺序执行。任何 middleware 或 endpoint 异常都会写入当前 pipeline 的 `ObservableInstanceTracker`，随后继续向调用者抛出。

## 4. 宿主生命周期

1. `builder.AddMonica(...)` 记录 `AddDataChannel().UseSetup<TSetup>()`。
2. Monica 验证 `ModuleDataChannel` 声明的 `channel-setup` 必需能力；缺少 `UseSetup<TSetup>()` 时启动失败。
3. `app.UseMonica()` 调用 setup，并把注册声明 materialize 为 `DataChannel`。
4. `app.MapMonica()` 映射 provider 与模块贡献的 endpoint。
5. `DataChannelInitializerService` 使用 `InitThreadCount` 限制并行度并初始化 endpoint。
6. graceful shutdown 释放所有 materialized pipeline endpoint。

setup 只在第 3 步持有 registrar；运行期代码不得缓存 registrar 并尝试动态新增 channel。

## 5. Provider 边界

当前 Labs 核心包提供 Kafka、ActiveMQ、TCP、UDP 与 default endpoint。Dapr Binding 位于独立的 `Monica.DataChannel.Dapr` 包；仅由显式引用该包并调用 `AddDataChannel().UseDaprBindings()` 的宿主启用，核心包不再传递引用 Dapr SDK。

Provider 负责：

- 校验自己的 `CommunicationOptions`；
- 建立或释放外部连接；
- 把外部消息包装成 `ChannelDataContext`；
- 遵守 pipeline 的完成、取消与异常语义。

DataChannel 不替外部系统提供 durability、broker 配置、secret 管理或跨实例协调。生产部署必须根据所选 provider 单独设计这些能力。

### Dapr route

显式启用 Dapr 集成后，同一宿主中，一个 input binding route 只能归属于一个 `Monica.DataChannel.Dapr.Providers.DaprBindingEndpoint`。route claim 存在当前 `IApplicationBuilder.Properties` 中，不会污染同一进程内的其他宿主。迁移方式见[绑定集成契约](../skills/monica-infra-messaging/references/data-channels.md#optional-dapr-binding-integration)。

### TCP runtime

`TcpConnectionRuntime` 是当前宿主的 singleton，拥有 outbound client、listener、accepted connection group 与 failover state。不同宿主即使复用同名 connection key，也不会共享 socket 或角色状态。

## 6. 运维与安全

启用 Minimal API 后，模块可公开 channel 状态、异常历史、清理和 reinitialize endpoint。它们属于运维控制面，生产环境必须配置鉴权、网络边界和审计。

`AddDataChannelUI()` 提供可选运维页面。`PipelineInfoDisplayMiddlewareBase` 的统计快照可在该页面展示，但不应存放 secret、大对象或高基数字段。

## 7. 配置

| 属性 | 默认值 | 约束 | 影响 |
|---|---:|---|---|
| `RecentExceptionToKeep` | `10` | 大于 `0` | 单个 pipeline 保留的最近异常数量。 |
| `InitThreadCount` | `10` | 大于 `0` | 启动时并行初始化 channel 的上限。 |
| `EnableMinimalApi` | 继承模块系统默认值 | 可选 | 是否映射 DataChannel 运维 API。 |

完整用户文档见 `../Monica.Docs/docs/zh-CN/modules/data-channel/`。
