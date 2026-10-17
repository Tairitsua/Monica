using System.Globalization;
using Monica.Guide;

namespace Monica.Guide.App;

/// <summary>Per-run setup UI state: language, wizard inputs, and the latest previewed plan.</summary>
public sealed class SetupSession
{
    private const string Chinese = "zh-CN";
    private const string English = "en-US";

    public static readonly IReadOnlyList<string> Languages = [Chinese, English];

    public SetupSession()
    {
        Language = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : English;
    }

    public string Language { get; private set; }

    public event Action? LanguageChanged;

    /// <summary>
    /// The product this wizard session operates on. Choosing a bundle of another registered
    /// product switches the session so plan, port, update, and integration surfaces all
    /// follow one definition.
    /// </summary>
    public AgentProductDefinition CurrentProduct { get; private set; } = KnownAgentProducts.Monica;

    public string BundlePath { get; set; } = string.Empty;

    /// <summary>Selected install targets as "environment::target" keys, one checkbox each.</summary>
    public HashSet<string> SelectedTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Port { get; set; } = string.Empty;

    public string? PlanDigest { get; set; }

    /// <summary>The bundle staged by the update page, kept for its configure preview/apply flow.</summary>
    public SetupBundleView? StagedUpdateBundle { get; set; }

    public IReadOnlyList<SetupAgentPresence>? PresenceCache { get; set; }

    /// <summary>True after host detection ran once, so revisits keep the user's target choices.</summary>
    public bool PresenceChecked { get; set; }

    public int? CachedPort { get; set; }

    /// <summary>
    /// The one full dashboard diagnosis of this wizard run. Host and runtime probing shells
    /// out to agent CLIs and takes seconds, so it starts in the background at startup,
    /// serves every later surface from this cache, and is replaced only by an explicit
    /// refresh or a product switch. Replacements raise <see cref="DashboardChanged"/> so
    /// layout-level surfaces (the drift banner) repaint with the fresh result.
    /// </summary>
    public SetupDashboardView? DashboardCache
    {
        get => _dashboardCache;
        set
        {
            _dashboardCache = value;
            DashboardChanged?.Invoke();
        }
    }

    private SetupDashboardView? _dashboardCache;

    /// <summary>Raised whenever the dashboard cache is replaced or cleared.</summary>
    public event Action? DashboardChanged;

    public void ToggleLanguage()
    {
        Language = Language == Chinese ? English : Chinese;
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// Switches the wizard to one product definition, resetting every product-derived input
    /// (port default, staged update, approved plan digest, cached dashboard) so the next
    /// load observes the new product. A no-op for the same product.
    /// </summary>
    public void SwitchProduct(AgentProductDefinition product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (ReferenceEquals(product, CurrentProduct))
        {
            return;
        }

        CurrentProduct = product;
        Port = product.DefaultPort?.ToString() ?? string.Empty;
        PlanDigest = null;
        StagedUpdateBundle = null;
        DashboardCache = null;
        CachedPort = null;
    }

    /// <summary>Resolves one localized setup label for the current language.</summary>
    public string T(string key)
        => SetupText.Resolve(Language, key);
}

/// <summary>Static two-language label catalog for the setup UI.</summary>
public static class SetupText
{
    private const string Fallback = "en-US";

    private static readonly Dictionary<string, string> ChineseText = new()
    {
        ["skip-content"] = "跳至主要内容",
        ["switch-language"] = "切换语言",
        ["integration-immediate"] = "这些设置会立即保存，无需预览安装计划。",
        ["navigation"] = "指南导航",
        ["switch-light"] = "切换浅色",
        ["switch-dark"] = "切换深色",
        ["dashboard-sub"] = "查看安装状态、工作区就绪情况与宿主观测。",
        ["environment"] = "本机环境",
        ["diagnosis-title"] = "配置诊断",
        ["checks-empty"] = "未返回诊断检查。",
        ["hosts-empty"] = "尚未记录 Agent 宿主。",
        ["workspaces-none"] = "尚未注册工作区",
        ["diagnosis-errors"] = "错误",
        ["diagnosis-errors-one"] = "错误",
        ["diagnosis-warnings"] = "警告",
        ["diagnosis-warnings-one"] = "警告",
        ["diagnosis-checked"] = "检查",
        ["diagnosis-checked-one"] = "检查",
        ["title"] = "Monica Guide",
        ["dashboard"] = "总览",
        ["install"] = "安装 / 升级",
        ["uninstall"] = "卸载",
        ["refresh"] = "刷新",
        ["installed-version"] = "已安装版本",
        ["not-installed"] = "未安装",
        ["bundle-root"] = "安装目录",
        ["cockpit-status"] = "工作台状态",
        ["running"] = "运行中",
        ["stopped"] = "未运行",
        ["detected-agents"] = "检测到的宿主",
        ["detecting-hosts"] = "正在检测宿主…",
        ["redetect-hosts"] = "重新检测",
        ["redetect-hosts-note"] = "宿主检测结果会持久保存;仅在此手动重新检测,日常加载与刷新不会重复探测。",
        ["detected-at"] = "检测于 {0}",
        ["detection-incomplete"] = "检测不完整(悬停查看)",
        ["detection-failed"] = "检测失败(悬停查看)",
        ["status-checks"] = "配置状态",
        ["health-checks"] = "健康检查",
        ["go-install"] = "安装或升级",
        ["go-uninstall"] = "卸载",
        ["start-cockpit"] = "启动工作台",
        ["open-cockpit"] = "打开工作台",
        ["bundle-path"] = "发布包目录(bundle 根目录,含 setup/ 或 app/)",
        ["validate"] = "校验",
        ["bundle-valid"] = "发布包有效",
        ["bundle-no-manifest"] = "目录有效,但没有 release manifest(开发构建)",
        ["select-targets"] = "选择要安装的目标",
        ["target-hint"] = "shared (~/.agents/skills) 适用于 Codex 等大多数 Agent 兼容宿主;claude (~/.claude/skills) 是 Claude Code 的专属发现目录。",
        ["port"] = "工作台端口",
        ["preview-plan"] = "预览计划",
        ["execute-install"] = "执行安装",
        ["preview-uninstall"] = "预览卸载",
        ["execute-uninstall"] = "执行卸载",
        ["confirm-uninstall"] = "我确认要移除以上列出的配置",
        ["plan-changes"] = "变更",
                ["plan-noop"] = "当前配置已完全一致,无需任何变更。",
        ["digest-note"] = "计划已锁定(digest 校验),执行时如有漂移会自动拒绝。",
        ["applied"] = "已执行",
        ["apply-failed"] = "执行未完成",
        ["bundle-missing-exe"] = "所选目录不是有效的发布包,请选择解压后的发布包根目录。",
        ["data-retention-note"] = "卸载只移除宿主配置与技能投影;产品数据(工作区、缓存、事务)保留在用户数据目录(Windows 为 %LOCALAPPDATA%\\<产品>,Linux 为 ~/.local/share/<产品>,macOS 为 ~/Library/Application Support/<产品>)。",
        ["ok"] = "正常",
        ["warning"] = "注意",
        ["error"] = "错误",
        ["action-replace-skill-directory"] = "替换技能目录",
        ["action-delete-skill-directory"] = "删除技能目录",
        ["action-write-file"] = "写入文件",
        ["action-delete-file"] = "删除文件",
        ["targets-required"] = "请至少选择一个安装目标。",
        ["port-invalid"] = "端口必须是 1-65535 之间的数字。",
        ["operating"] = "正在执行…",
        ["diagnosing"] = "正在诊断…",
        ["elapsed"] = "已用时",
        ["update"] = "更新",
        ["check-now"] = "检查更新",
        ["checking"] = "正在检查…",
        ["up-to-date"] = "已是最新版本。",
        ["update-available"] = "有可用更新",
        ["latest-version"] = "最新版本",
        ["release-notes"] = "发布说明",
        ["download-verify"] = "下载并校验",
        ["download-again"] = "重新下载",
        ["staged-ready"] = "新版本已下载并通过校验,可以预览升级计划。",
        ["update-flow-note"] = "升级会停止运行中的工作台,应用后自动重启;旧版本目录会保留,可随时回滚。",
        ["phase-download"] = "下载",
        ["phase-verify"] = "校验",
        ["phase-extract"] = "解压",
        ["phase-validate"] = "验证",
        ["rollback-title"] = "可回滚版本",
        ["rollback-hint"] = "以下历史版本仍保留在本机,切换即可回滚(同样走预览与确认)。",
        ["rollback"] = "回滚到此版本",
        ["update-restart-note"] = "正在重启工作台…",
        ["integration"] = "桌面集成",
        ["desktop-shortcut"] = "桌面快捷方式",
        ["startmenu-shortcut"] = "开始菜单快捷方式",
        ["autostart"] = "开机自动启动",
        ["integration-note"] = "快捷方式会打开工作台界面;自启在登录时后台启动,不弹出浏览器。",
        ["uninstall-integration-note"] = "卸载同时移除本产品自有的快捷方式与自启项(如有)。",
        ["stop-cockpit"] = "停止工作台",
        ["summary-ok"] = "一切就绪",
        ["summary-attention"] = "项需要关注",
        ["summary-attention-one"] = "项需要关注",
        ["drift-banner"] = "技能投影漂移:以下目标的已装技能与安装记录或配置的发布包不一致;更新技能可重新收敛。",
        ["drift-kind-global"] = "全局",
        ["drift-kind-workspace"] = "工作区",
        ["drift-banner-cta"] = "查看详情",
        ["version"] = "版本",
        ["location"] = "位置",
        ["status"] = "状态",
        ["step-source"] = "发布包",
        ["step-options"] = "宿主与端口",
        ["step-execute"] = "预览并执行",
        ["install-to"] = "将安装",
        ["currently-installed"] = "当前已装",
        ["choose-folder-hint"] = "粘贴或输入解压后的发布包根目录(包含 setup/ 或 app/ 文件夹)。",
        ["cancel"] = "取消",
        ["installed-targets"] = "已安装的技能目标",
        ["nothing-installed"] = "当前没有已记录的安装。",
        ["remove-target"] = "移除此目标",
        ["uninstall-everything"] = "全部卸载",
        ["workspaces"] = "工作区",
        ["workspaces-sub"] = "Monica 技能默认安装到各项目自己的目录;这里登记每个已初始化的工作区,并可安装、更新或忘记它们。",
        ["workspaces-empty"] = "还没有登记的工作区。在下方输入一个项目目录开始初始化。",
        ["ws-skills"] = "个技能",
        ["ws-instructions"] = "指令块",
        ["ws-install"] = "安装技能",
        ["ws-update"] = "更新技能",
        ["ws-forget"] = "忘记工作区",
        ["ws-add-title"] = "添加工作区",
        ["ws-path"] = "项目目录",
        ["ws-path-hint"] = "粘贴项目根目录;向导会检测项目类型(profile)、能力与框架版本。",
        ["ws-profile"] = "确认项目类型",
        ["ws-profile-desc-application"] = "在不动框架源码的前提下,基于 Monica 构建自己的应用;安装应用与 ProjectUnit 开发技能。",
        ["ws-profile-desc-extension-author"] = "开发独立发布、通过 NuGet 引用 Monica 的扩展包;安装框架、架构与扩展开发技能。",
        ["ws-profile-desc-framework-contributor"] = "在官方框架检出中开发和测试 Monica 源码;安装框架、架构、需求设计与测试技能。",
        ["ws-profile-hint"] = "检测到的候选:",
        ["ws-candidate"] = "候选",
        ["ws-architecture"] = "应用架构",
        ["ws-application-ui"] = "应用界面",
        ["ws-application-backend"] = "纯后端",
        ["ws-application-backend-desc"] = "安装应用架构、业务开发与基础设施技能。",
        ["ws-application-with-ui"] = "带业务 UI",
        ["ws-application-with-ui-desc"] = "增加应用 UI 架构、页面开发、主题与本地化技能；业务界面可拥有自己的布局和导航。",
        ["ws-init-preview"] = "预览初始化",
        ["ws-apply-install"] = "向该工作区安装技能",
        ["ws-apply-forget"] = "移除该工作区的 Monica 内容",
        ["ws-global-guide-title"] = "{0} 全局安装",
        ["ws-global-guide-note"] = "启用后 {0} 只安装到用户级目录一次,更新工作区会移除工作区内的副本;禁用后更新工作区会重新安装副本。",
        ["ws-rules-title"] = "托管规则",
        ["ws-rules-hint"] = "被关闭的规则不会写入该工作区 AGENTS.md 的托管块;开关变更在下次「更新技能」时经预览确认后写入工作区。",
        ["ws-rules-pending"] = "有未应用的开关变更",
        ["ws-rules-empty"] = "该工作区没有可配置的托管规则。",
        ["ws-preview-title"] = "指令注入示例",
        ["ws-preview-note"] = "初始化或更新任一工作区时,向导会把下方内容写入该工作区 AGENTS.md 的托管标记之间(内容为机器稳定英文,段落由项目类型与本机状态生成);其中「第一方源码」与「Issue 上报策略」两个段落由下列开关实时决定,变更在下一次初始化或更新时收敛到工作区。",
        ["ws-preview-note-plain"] = "初始化或更新任一工作区时,向导会把下方内容写入该工作区 AGENTS.md 的托管标记之间(内容为机器稳定英文,由项目类型生成)。本产品只注入自己的工作流段落;「第一方源码」与「Issue 上报策略」等机器级段落由 Monica 安装器统一管理,不会在此重复注入。",
        ["ws-preview-issue-mode"] = "Issue 上报模式",
        ["ws-preview-profile"] = "示例 Profile:{0}",
        ["ws-preview-unavailable"] = "本机还没有已安装的发布包目录,暂无法生成示例;安装后此处会显示将要写入的托管块。",
        ["ws-already-initialized"] = "已初始化",
        ["ws-initialized-banner"] = "已初始化:项目类型 {0} · 能力 {1} · 由 {2} 初始化",
        ["ws-closure-title"] = "将安装的技能",
        ["ws-ambiguous-note"] = "源码扫描结论存在歧义;若上面记录的项目类型不符合预期,可重新选择。",
        ["ws-reselect-profile"] = "重新选择项目类型",
        ["ws-profile-required"] = "请选择项目类型。",
        ["ws-architecture-required"] = "application 类型需要选择一种架构。",
        ["ws-inspecting"] = "正在检测项目类型、能力与框架版本…",
        ["ws-detect-not-directory"] = "该路径不是存在的目录,请检查后重试。",
        ["ws-detect-framework"] = "检测到 Monica 框架仓库。",
        ["ws-detect-extension"] = "检测到 Monica 扩展特征。",
        ["ws-detect-application"] = "检测到 Monica 包或项目引用。",
        ["ws-detect-ambiguous-both"] = "同时检测到应用与扩展特征;请显式选择项目类型。",
        ["ws-detect-ambiguous-unscanned"] = "检测到应用特征,但有界源码扫描无法排除扩展;请显式选择项目类型。",
        ["ws-detect-ambiguous-exhausted"] = "在有界源码扫描结束前没有发现决定性特征;请显式选择项目类型。",
        ["ws-detect-ambiguous-none"] = "未发现规范仓库标识或特征文件;请显式选择项目类型。",
        ["workspace-summary"] = "工作区",
        ["workspace-summary-healthy"] = "个健康",
        ["global-targets"] = "整机全局安装(可选)",
        ["global-targets-hint"] = "默认安装到项目目录;只有需要跨所有项目使用时才选择全局目录。",
        ["ws-group"] = "项目工作区",
        ["global-group"] = "全局目标",
        ["browse"] = "浏览…",
        ["browse-bundle-title"] = "选择发布包根目录(包含 setup/ 或 app/ 的文件夹)",
        ["browse-workspace-title"] = "选择项目根目录",
        ["browse-source-title"] = "选择源码检出目录",
        ["sources"] = "源码与上报",
        ["sources-sub"] = "在机器全局记录由当前产品声明并经过验证的第一方源码检出,用于 issue 上报、API 查询、问题排查与源码参考;绑定只是只读定位器,绝不授予写权限,且被本机所有产品共享。",
        ["src-issue-title"] = "Issue 上报",
        ["src-issue-mode-prepare"] = "准备草稿",
        ["src-issue-desc-prepare"] = "允许智能体准备本地 issue 草稿;创建 issue、分支或推送等远程操作仍需当次确认。",
        ["src-issue-mode-ask"] = "每次询问",
        ["src-issue-desc-ask"] = "智能体在准备任何 issue 草稿前必须先征得同意。",
        ["src-issue-mode-never"] = "从不",
        ["src-issue-desc-never"] = "不准备、也不创建任何 issue。",
        ["src-issue-note"] = "此偏好只约束草稿准备阶段;持久化的偏好永远不构成对远程操作的授权。",
        ["src-proj-title"] = "工作区指令投影",
        ["src-proj-sources-title"] = "源码定位写入工作区指令",
        ["src-proj-sources-note"] = "启用后,初始化或更新工作区会把已绑定的第一方源码检出作为只读参考写入 AGENTS.md 管理块;禁用后下一次更新会移除该段落。",
        ["src-proj-issue-title"] = "Issue 上报策略写入工作区指令",
        ["src-proj-issue-note"] = "启用后,每个工作区的 AGENTS.md 管理块会携带本机的 issue 上报策略;禁用后下一次更新会移除该段落。",
        ["src-health-available"] = "可用",
        ["src-health-moved"] = "已移动",
        ["src-health-missing"] = "路径缺失",
        ["src-health-invalid"] = "无效检出",
        ["src-dirty"] = "有本地改动",
        ["src-commit"] = "commit",
        ["src-unbound-note"] = "{0} 尚未绑定全局源码检出。",
        ["src-path-hint"] = "填入已有的本地检出(开发者可直接用本机的源码检出);向导会验证它是指定仓库的规范克隆,并记录其精确 commit。",
        ["src-bind-preview"] = "预览绑定",
        ["src-rebind"] = "更新到当前 commit",
        ["src-unbind"] = "解除绑定",
        ["src-apply-bind"] = "记录全局源码绑定",
        ["src-apply-unbind"] = "移除全局源码绑定",
    };

    private static readonly Dictionary<string, string> EnglishText = new()
    {
        ["skip-content"] = "Skip to content",
        ["switch-language"] = "Switch language",
        ["integration-immediate"] = "These preferences save immediately, without an installation preview.",
        ["navigation"] = "Guide navigation",
        ["switch-light"] = "Switch to light",
        ["switch-dark"] = "Switch to dark",
        ["dashboard-sub"] = "Installation, workspace readiness and host observations.",
        ["environment"] = "Your environment",
        ["diagnosis-title"] = "Configuration diagnosis",
        ["checks-empty"] = "No diagnostic checks were returned.",
        ["hosts-empty"] = "No agent hosts were recorded.",
        ["workspaces-none"] = "No workspaces registered",
        ["diagnosis-errors"] = "Errors",
        ["diagnosis-errors-one"] = "Error",
        ["diagnosis-warnings"] = "Warnings",
        ["diagnosis-warnings-one"] = "Warning",
        ["diagnosis-checked"] = "Checks",
        ["diagnosis-checked-one"] = "Check",
        ["title"] = "Monica Guide",
        ["dashboard"] = "Overview",
        ["install"] = "Install / Update",
        ["uninstall"] = "Uninstall",
        ["refresh"] = "Refresh",
        ["installed-version"] = "Installed version",
        ["not-installed"] = "Not installed",
        ["bundle-root"] = "Install location",
        ["cockpit-status"] = "Cockpit status",
        ["running"] = "Running",
        ["stopped"] = "Stopped",
        ["detected-agents"] = "Detected hosts",
        ["detecting-hosts"] = "Detecting hosts…",
        ["redetect-hosts"] = "Re-detect",
        ["redetect-hosts-note"] = "Host detection results are persisted; this manual re-detection is the only trigger, so normal loads and refreshes never probe again.",
        ["detected-at"] = "detected {0}",
        ["detection-incomplete"] = "detection incomplete (hover for detail)",
        ["detection-failed"] = "detection failed (hover for detail)",
        ["status-checks"] = "Configuration status",
        ["health-checks"] = "Health checks",
        ["go-install"] = "Install or update",
        ["go-uninstall"] = "Uninstall",
        ["start-cockpit"] = "Start cockpit",
        ["open-cockpit"] = "Open cockpit",
        ["bundle-path"] = "Release bundle directory (bundle root containing setup/ or app/)",
        ["validate"] = "Validate",
        ["bundle-valid"] = "Bundle is valid",
        ["bundle-no-manifest"] = "Directory is valid but has no release manifest (development build)",
        ["select-targets"] = "Choose the install targets",
        ["target-hint"] = "shared (~/.agents/skills) serves Codex and most Agent Skills hosts; claude (~/.claude/skills) is Claude Code's own discovery directory.",
        ["port"] = "Cockpit port",
        ["preview-plan"] = "Preview plan",
        ["execute-install"] = "Apply install",
        ["preview-uninstall"] = "Preview uninstall",
        ["execute-uninstall"] = "Apply uninstall",
        ["confirm-uninstall"] = "I confirm removing the configuration listed above",
        ["plan-changes"] = "changes",
                ["plan-noop"] = "Everything already matches; no changes are needed.",
        ["digest-note"] = "The plan is digest-locked; drift at apply time is rejected automatically.",
        ["applied"] = "Applied",
        ["apply-failed"] = "Not completed",
        ["bundle-missing-exe"] = "The selected directory is not a valid release bundle; choose an extracted release bundle root.",
        ["data-retention-note"] = "Uninstall removes host configuration and skill projections only; product data (workspaces, caches, transactions) stays in the user data root (%LOCALAPPDATA%\\<product> on Windows, ~/.local/share/<product> on Linux, ~/Library/Application Support/<product> on macOS).",
        ["ok"] = "OK",
        ["warning"] = "Warning",
        ["error"] = "Error",
        ["action-replace-skill-directory"] = "Replace skill directories",
        ["action-delete-skill-directory"] = "Delete skill directories",
        ["action-write-file"] = "Write file",
        ["action-delete-file"] = "Delete file",
        ["targets-required"] = "Select at least one install target.",
        ["port-invalid"] = "The port must be a number between 1 and 65535.",
        ["operating"] = "Working…",
        ["diagnosing"] = "Running diagnostics…",
        ["elapsed"] = "Elapsed",
        ["update"] = "Update",
        ["check-now"] = "Check for updates",
        ["checking"] = "Checking…",
        ["up-to-date"] = "You are up to date.",
        ["update-available"] = "Update available",
        ["latest-version"] = "Latest version",
        ["release-notes"] = "Release notes",
        ["download-verify"] = "Download and verify",
        ["download-again"] = "Download again",
        ["staged-ready"] = "The new release is downloaded and verified; preview the upgrade plan next.",
        ["update-flow-note"] = "The upgrade stops a running cockpit and restarts it afterwards; the previous release directory is kept for rollback.",
        ["phase-download"] = "Download",
        ["phase-verify"] = "Verify",
        ["phase-extract"] = "Extract",
        ["phase-validate"] = "Validate",
        ["rollback-title"] = "Rollback candidates",
        ["rollback-hint"] = "These releases are still on disk; switching goes through the same preview and confirmation.",
        ["rollback"] = "Switch to this version",
        ["update-restart-note"] = "Restarting the cockpit…",
        ["integration"] = "Desktop integration",
        ["desktop-shortcut"] = "Desktop shortcut",
        ["startmenu-shortcut"] = "Start menu shortcut",
        ["autostart"] = "Start automatically at logon",
        ["integration-note"] = "Shortcuts open the cockpit UI; autostart runs it in the background at logon without opening a browser.",
        ["uninstall-integration-note"] = "Uninstall also removes product-owned shortcuts and the autostart entry when present.",
        ["stop-cockpit"] = "Stop cockpit",
        ["summary-ok"] = "All good",
        ["summary-attention"] = "items need attention",
        ["summary-attention-one"] = "item needs attention",
        ["drift-banner"] = "Skill projection drift — the installed skills of the targets below diverge from their recorded installation or the configured release; updating the skills reconverges them.",
        ["drift-kind-global"] = "global",
        ["drift-kind-workspace"] = "workspace",
        ["drift-banner-cta"] = "Review on Overview",
        ["version"] = "Version",
        ["location"] = "Location",
        ["status"] = "Status",
        ["step-source"] = "Release bundle",
        ["step-options"] = "Hosts and port",
        ["step-execute"] = "Preview and apply",
        ["install-to"] = "Will install",
        ["currently-installed"] = "Currently installed",
        ["choose-folder-hint"] = "Paste the extracted release bundle root (the folder containing setup/ or app/).",
        ["cancel"] = "Cancel",
        ["installed-targets"] = "Installed skill targets",
        ["nothing-installed"] = "No installation is recorded yet.",
        ["remove-target"] = "Remove this target",
        ["uninstall-everything"] = "Uninstall everything",
        ["workspaces"] = "Workspaces",
        ["workspaces-sub"] = "Monica skills install into each project's own directories by default; every initialized workspace is registered here for installing, updating, or forgetting.",
        ["workspaces-empty"] = "No workspaces are registered yet. Enter a project directory below to initialize one.",
        ["ws-skills"] = "skills",
        ["ws-instructions"] = "instructions",
        ["ws-install"] = "Install skills",
        ["ws-update"] = "Update skills",
        ["ws-forget"] = "Forget workspace",
        ["ws-add-title"] = "Add a workspace",
        ["ws-path"] = "Project directory",
        ["ws-path-hint"] = "Paste a project root; the wizard detects the profile, capabilities, and framework version.",
        ["ws-profile"] = "Confirm the profile",
        ["ws-profile-desc-application"] = "Build your own application on Monica without touching framework source; installs the application and ProjectUnit skills.",
        ["ws-profile-desc-extension-author"] = "Develop independently released extension packages that consume Monica through NuGet; installs the framework, architecture, and extension skills.",
        ["ws-profile-desc-framework-contributor"] = "Develop and test Monica framework source in the canonical checkout; installs the framework, architecture, requirement-design, and testing skills.",
        ["ws-profile-hint"] = "Detected candidate:",
        ["ws-candidate"] = "candidate",
        ["ws-architecture"] = "Application architecture",
        ["ws-application-ui"] = "Application interface",
        ["ws-application-backend"] = "Backend only",
        ["ws-application-backend-desc"] = "Install application architecture, business development, and infrastructure skills.",
        ["ws-application-with-ui"] = "With application UI",
        ["ws-application-with-ui-desc"] = "Also install application UI architecture, page development, themes, and localization skills. The product owns its layout and navigation.",
        ["ws-init-preview"] = "Preview initialization",
        ["ws-apply-install"] = "Install skills into this workspace",
        ["ws-apply-forget"] = "Remove Monica from this workspace",
        ["ws-global-guide-title"] = "Global {0}",
        ["ws-global-guide-note"] = "When enabled, {0} installs once at the user level and updating a workspace removes its local copy; when disabled, updates reinstall the copy.",
        ["ws-rules-title"] = "Managed rules",
        ["ws-rules-hint"] = "Disabled rules are omitted from this workspace's managed AGENTS.md block; switch changes apply through the next skill update's preview-confirmed apply.",
        ["ws-rules-pending"] = "Switch changes pending",
        ["ws-rules-empty"] = "This workspace has no switchable managed rules.",
        ["ws-preview-title"] = "Instruction injection example",
        ["ws-preview-note"] = "Initializing or updating any workspace writes the content below between the managed markers of that workspace's AGENTS.md (machine-stable English, generated from the profile and this machine's state); the first-party source and issue policy sections follow the controls below, and changes converge into workspaces on the next initialization or update.",
        ["ws-preview-note-plain"] = "Initializing or updating any workspace writes the content below between the managed markers of that workspace's AGENTS.md (machine-stable English, generated from the profile). This product injects only its own workflow sections; machine-global sections such as first-party source locators and the issue policy are owned by the Monica guide and never duplicated here.",
        ["ws-preview-issue-mode"] = "Issue reporting mode",
        ["ws-preview-profile"] = "example profile: {0}",
        ["ws-preview-unavailable"] = "No installed release catalog defines managed instructions yet; the example appears here after installation.",
        ["ws-already-initialized"] = "already initialized",
        ["ws-initialized-banner"] = "Already initialized: profile {0} · capabilities {1} · initialized by {2}",
        ["ws-closure-title"] = "Skills to install",
        ["ws-ambiguous-note"] = "The source scan was ambiguous; reselect the profile if the recorded one does not match.",
        ["ws-reselect-profile"] = "Change profile",
        ["ws-profile-required"] = "Select a profile.",
        ["ws-architecture-required"] = "The application profile requires one architecture.",
        ["ws-inspecting"] = "Detecting the profile, capabilities, and framework version…",
        ["ws-detect-not-directory"] = "The path is not an existing directory; check it and try again.",
        ["ws-detect-framework"] = "Monica framework repository detected.",
        ["ws-detect-extension"] = "Monica extension characteristics detected.",
        ["ws-detect-application"] = "Monica package or project references detected.",
        ["ws-detect-ambiguous-both"] = "Both application and extension characteristics were detected; select a profile explicitly.",
        ["ws-detect-ambiguous-unscanned"] = "Application characteristics were detected, but bounded source scanning could not rule out an extension; select a profile explicitly.",
        ["ws-detect-ambiguous-exhausted"] = "No decisive characteristics were found before the bounded source scan was exhausted; select a profile explicitly.",
        ["ws-detect-ambiguous-none"] = "No canonical repository identity or characteristic files were found; select a profile explicitly.",
        ["workspace-summary"] = "Workspaces",
        ["workspace-summary-healthy"] = "healthy",
        ["global-targets"] = "Machine-wide global install (optional)",
        ["global-targets-hint"] = "Project directories are the default; choose global catalogs only when the skills must serve every project.",
        ["ws-group"] = "Project workspaces",
        ["global-group"] = "Global targets",
        ["browse"] = "Browse…",
        ["browse-bundle-title"] = "Choose the release bundle root (the folder containing setup/ or app/)",
        ["browse-workspace-title"] = "Choose the project root",
        ["browse-source-title"] = "Choose the source checkout",
        ["sources"] = "Sources",
        ["sources-sub"] = "Machine-global records of catalog-declared, verified first-party source checkouts used for issue reporting, API lookup, troubleshooting, and source reference; a binding is a lookup-only locator that never grants write access and is shared by every product on this machine.",
        ["src-issue-title"] = "Issue reporting",
        ["src-issue-mode-prepare"] = "Prepare drafts",
        ["src-issue-desc-prepare"] = "Agents may draft local issue artifacts; creating issues, branches, or pushes still requires per-action approval.",
        ["src-issue-mode-ask"] = "Ask every time",
        ["src-issue-desc-ask"] = "Agents must ask before preparing any issue draft.",
        ["src-issue-mode-never"] = "Never",
        ["src-issue-desc-never"] = "Do not prepare or create any issues.",
        ["src-issue-note"] = "The mode bounds preparation only; a persisted preference never authorizes a remote action.",
        ["src-proj-title"] = "Workspace instruction projection",
        ["src-proj-sources-title"] = "Project source locators into workspace instructions",
        ["src-proj-sources-note"] = "When enabled, initializing or updating a workspace writes the bound first-party source checkouts into the AGENTS.md managed block as lookup-only references; disabling removes the section on the next update.",
        ["src-proj-issue-title"] = "Project the issue policy into workspace instructions",
        ["src-proj-issue-note"] = "When enabled, every workspace's AGENTS.md managed block carries this machine's issue-reporting policy; disabling removes the section on the next update.",
        ["src-health-available"] = "available",
        ["src-health-moved"] = "moved",
        ["src-health-missing"] = "path missing",
        ["src-health-invalid"] = "invalid checkout",
        ["src-dirty"] = "local changes",
        ["src-commit"] = "commit",
        ["src-unbound-note"] = "{0} has no global source binding yet.",
        ["src-path-hint"] = "Enter an existing local checkout (developers can point straight at their own source checkout); the wizard verifies it is a canonical clone of the repository and records its exact commit.",
        ["src-bind-preview"] = "Preview binding",
        ["src-rebind"] = "Rebind at current commit",
        ["src-unbind"] = "Unbind",
        ["src-apply-bind"] = "Record the global source binding",
        ["src-apply-unbind"] = "Remove the global source binding",
    };

    /// <summary>Resolves one label for the requested language, falling back to English and then the key.</summary>
    public static string Resolve(string language, string key)
    {
        var catalog = language == "zh-CN" ? ChineseText : EnglishText;
        return catalog.TryGetValue(key, out var value)
               || EnglishText.TryGetValue(key, out value)
            ? value
            : key;
    }
}
