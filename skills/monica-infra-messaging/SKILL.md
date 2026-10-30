---
name: monica-infra-messaging
description: Use when integrating Monica EventBus, RPC clients, DataChannel pipelines, or SignalR hubs and diagnosing their transport, delivery, and provider configuration.
---

# Monica messaging

Choose the communication boundary before the transport; database-coupled delivery also needs the persistence skill's [transactional events](../monica-infra-persistence/references/transactional-events.md).

| Task | Read |
| --- | --- |
| Notify typed handlers, bound processing retries, or configure dead-letter handling | [EventBus](references/event-bus.md) |
| Inspect EventBus subscriptions, transport status, providers, or diagnostic listeners | [EventBus diagnostics](references/event-bus-diagnostics.md) |
| Configure the Kafka console or inspect broker clusters, topics, consumer groups, and offset samples | [Kafka console](references/kafka-console.md) |
| Call another application's published request contract and await a result | [RPC client](references/rpc.md) |
| Recover structured remote failures from Dapr actor calls | [Dapr diagnostics](references/dapr-diagnostics.md) |
| Send through or inspect a named bidirectional pipeline owned by one host | [DataChannel](references/data-channels.md) |
| Invoke typed methods on connected Web clients or inspect a browser debug session | [SignalR](references/signalr.md) |

For application contract placement, use [ProjectUnit development](../monica-application-project-unit-development/SKILL.md). For framework transport or module implementation, use `$monica-framework`, then `$monica-development`.

If a routed authoring skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md`.
