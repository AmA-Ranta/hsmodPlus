using ArenaBoxBot;

var checks = 0;
void Check(bool value, string why) { checks++; if (!value) throw new InvalidOperationException(why); }

Check(ArenaModeCatalog.All.Length == 5, "插件必须恰好支持 5 大模式");
Check(string.Join("/", ArenaModeCatalog.All) == "标准/狂野/休闲/酒馆战棋/竞技场", "五模式顺序必须稳定");
foreach (var mode in ArenaModeCatalog.All) Check(ArenaModeCatalog.IsKnown(mode), "已知模式判定失败：" + mode);
Check(!ArenaModeCatalog.IsKnown("竞技场2"), "未知模式不得通过已知判定");
Check(!ArenaModeCatalog.IsKnown(""), "空模式不得通过已知判定");

Check(ArenaModeCatalog.ToNative("标准") == "Standard", "标准映射错误");
Check(ArenaModeCatalog.ToNative("狂野") == "Wild", "狂野映射错误");
Check(ArenaModeCatalog.ToNative("休闲") == "Casual", "休闲映射错误");
Check(ArenaModeCatalog.ToNative("酒馆战棋") == "Battlegrounds", "酒馆战棋映射错误");
Check(ArenaModeCatalog.ToNative("竞技场") == "Standard", "竞技场原生占位必须保持合法值");
Check(ArenaModeCatalog.ToDisplay("Standard") == "标准", "Standard 显示映射错误");
Check(ArenaModeCatalog.ToDisplay("Wild") == "狂野", "Wild 显示映射错误");
Check(ArenaModeCatalog.ToDisplay("Casual") == "休闲", "Casual 显示映射错误");
Check(ArenaModeCatalog.ToDisplay("Battlegrounds") == "酒馆战棋", "Battlegrounds 显示映射错误");
Check(ArenaModeCatalog.ToDisplay("Arena") == "竞技场", "Arena 显示映射错误");

var cycle = "标准";
var visited = new List<string>();
for (var i = 0; i < 5; i++) { visited.Add(cycle); cycle = ArenaModeCatalog.Next(cycle); }
Check(string.Join("/", visited) == string.Join("/", ArenaModeCatalog.All), "F11 五模式循环必须覆盖全部模式且回到起点");
Check(cycle == "标准", "五模式循环必须回到起点");
Check(ArenaModeCatalog.Next("未知") == "狂野", "未知模式的循环起点与标准一致");
Check(ArenaModeCatalog.Summary == "标准/狂野/休闲/酒馆战棋/竞技场", "状态摘要必须列出全部五模式");

Console.WriteLine($"PASS: {checks} five-mode catalog assertions (plugin is independent of the controller)");
