namespace Monica.Tool.Text;

/// <summary>
/// Shared text dictionaries used by parsing and normalization helpers.
/// </summary>
public static class TextMappings
{
    /// <summary>
    /// Maps supported Chinese punctuation symbols to their ASCII equivalents.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ZhToEnPunctuationDict { get; } =
        new Dictionary<string, string>
        {
            {"；", ";"}, {"。", "."}, {"：", ":"}, {"，", ","}, {"？", "?"}, {"！", "!"}, {"“", "\""}, {"”", "\""},
            {"（", "("}, {"）", ")"}, {"【", "["}, {"】", "]"}, {"《", "<"}, {"》", ">"}, {"…", "..."}, {"—", "-"},
            {"￥", "$"}, {"、", "\\"}, {"～", "~"}
        };
    /// <summary>
    /// Maps common boolean text literals to their boolean values.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> StringToBoolDict { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        {"yes", true}, {"no", false}, {"1", true}, {"0", false}, {"是", true}, {"否", false}, {"对", true},
        {"错", false}, {"不对", false}, {"on", true}, {"off", false}, {"非", false},{"not", false},
        {"none", false},{"true", true},{"false", false},{"正确", true},{"错误", false},{"open", true},{"close", false},
        {"disable", false},{"enable", true},{"启动", true},{"关闭", false},{"启用", true},{"开", true},{"关", false},
        {"开启", true},{"打开", true},{"t", true},{"f", false}
    };
    /// <summary>
    /// Maps Chinese math operator names to their symbolic forms.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ZhMathToSymbolMath { get; } = new Dictionary<string, string>()
    {
        {"加", "+"},{"减", "-"},{"乘", "*"},{"除","/"}
    };
    /// <summary>
    /// Keywords that express moving to the next page.
    /// </summary>
    public static readonly HashSet<string> PageNextList = new()
    {
        "下",
        "n",
        "next",
        "下一页",
        "翻页",
        "f",
        "forward"
    };
    /// <summary>
    /// Keywords that express moving to the previous page.
    /// </summary>
    public static readonly HashSet<string> PagePreviousList = new()
    {
        "上",
        "p",
        "previous",
        "上一页",
        "back",
        "b"
    };

    /// <summary>
    /// Keywords that indicate create actions in CRUD flows.
    /// </summary>
    public static readonly HashSet<string> CrudAddAction = new()
    {
        "add",
        "create",
        "增",
        "增加",
        "学",
        "学习",
        "添加",
        "加",
        "新增",
        "learn",
        "append",
        "a",
        "c",
        "post"
    };
    /// <summary>
    /// Keywords that indicate delete actions in CRUD flows.
    /// </summary>
    public static readonly HashSet<string> CrudDeleteAction = new()
    {
        "delete",
        "d",
        "remove",
        "删除",
        "丢弃",
        "删",
        "删掉",
        "忘记",
        "forget",
    };
    /// <summary>
    /// Keywords that indicate update actions in CRUD flows.
    /// </summary>
    public static readonly HashSet<string> CrudModifyAction = new()
    {
        "modify",
        "update",
        "修改",
        "更改",
        "更新",
        "u",
        "alter",
        "改",
        "put",
        "patch"
    };
    /// <summary>
    /// Keywords that indicate query actions in CRUD flows.
    /// </summary>
    public static readonly HashSet<string> CrudSearchAction = new()
    {
        "search",
        "retrieve",
        "检索",
        "搜索",
        "查询",
        "query",
        "r",
        "查",
        "get"
    };

    /// <summary>
    /// Common verb tokens used by linguistic heuristics.
    /// </summary>
    public static readonly HashSet<string> Verb = new()
    {
        "看",
        "望",
        "瞥",
        "视",
        "盯",
        "瞧",
        "窥",
        "瞄",
        "眺",
        "瞪",
        "瞅",
        "听",
        "咬",
        "吞",
        "吐",
        "吮",
        "吸",
        "啃",
        "喝",
        "吃",
        "咀",
        "嚼",
        "搀",
        "抱",
        "搂",
        "扶",
        "捉",
        "擒",
        "掐",
        "推",
        "拿",
        "抽",
        "撕",
        "摘",
        "拣",
        "捡",
        "打",
        "播",
        "击",
        "捏",
        "撒",
        "按",
        "弹",
        "撞",
        "提",
        "扭",
        "捶",
        "持",
        "揍",
        "披",
        "捣",
        "搜",
        "托",
        "举",
        "拖",
        "擦",
        "敲",
        "挖",
        "抛",
        "掘",
        "抬",
        "插",
        "扔",
        "写",
        "抄",
        "抓",
        "捧",
        "掷",
        "撑",
        "摊",
        "倒",
        "摔",
        "劈",
        "画",
        "搔",
        "撬",
        "挥",
        "揽",
        "挡",
        "捺",
        "抚",
        "搡",
        "拉",
        "摸",
        "拍",
        "摇",
        "剪",
        "拎",
        "拔",
        "拧",
        "拨",
        "舞",
        "握",
        "攥",
        "退",
        "进",
        "奔",
        "跑",
        "赶",
        "趋",
        "遁",
        "逃",
        "立",
        "站",
        "跨",
        "踢",
        "跳",
        "走",
        "蹬",
        "窜",
        "屈",
        "转",
        "蜷",
        "卧",
        "仰",
        "躺",
        "睡",
        "趴",
        "蹲",
        "弯",
        "弓",
        "挺",
        "俯",
        "说",
        "笑",
        "飞",
        "唱",
        "坐",
        "闻",
        "想",
        "爱",
        "恨",
        "念",
        "喜欢",
        "希望",
        "担",
        "讨",
        "觉",
        "思"
    };
}
