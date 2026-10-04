# 单便签窗口内 F3 查找功能技术方案与可行性评估

- **日期**：2026-10-03 (GMT+8)
- **状态**：设计归档 / 待评审决策
- **关联文档**：[`docs/SEARCH-MULTI-HIT-DESIGN-20261003.md`](file:///c:/Home/Projects/StickyNotes/docs/SEARCH-MULTI-HIT-DESIGN-20261003.md) / [`docs/APP-ARCHITECTURE.md`](file:///c:/Home/Projects/StickyNotes/docs/APP-ARCHITECTURE.md)

---

## 一、 需求背景与问题定义

在便签应用的使用场景中，除在「便签管理中心（`NotesListWindow`）」进行全局便签检索外，用户经常需要处理长内容便签，在**单个便签窗口（`NoteWindow`）**内进行就地查找与快速导航：

1. **场景一（窗口内就地查找）**：用户在独立便签窗口内查看或编辑长文时，希望按 `Ctrl+F` 或 `F3` 快速定位到目标关键词，并能在多个命中位置之间快速切换跳转（Next / Previous）。
2. **场景二（跨窗口搜索联动）**：用户从主列表搜索结果双击/按 Enter 进入便签后，已定位在第 1 处命中；若该便签内有多处匹配，用户希望在便签内直接按 `F3` / `Shift+F3` 顺次浏览后续所有匹配点，而无需频繁切回主列表。

---

## 二、 复杂度核验与技术限制分析

### 2.1 复杂度结论
做单个便签窗口内的 F3 查找，**整体工程复杂度属于【低到中（Low-Medium）】**，在可控范围内，不会对现有数据层和存储架构产生破坏性影响。

此前在 [`docs/SEARCH-MULTI-HIT-DESIGN-20261003.md`](file:///c:/Home/Projects/StickyNotes/docs/SEARCH-MULTI-HIT-DESIGN-20261003.md) §5 中将其归为“高风险”，前提是**“试图在 WPF 原生 `TextBox` 上自绘全文所有匹配项的黄色底纹装饰层”**。只要采用主流桌面编辑器（Windows 记事本、Chrome、VS Code）标准的**“光标选区循环跳跃（Next/Prev Selection）+ 视口平滑居中”**模型，复杂度便大幅下降。

### 2.2 现有工程基础设施（实证核验）
1. **精准定位与视口居中成熟稳定**：
   [`NoteWindow.xaml.cs:86-121`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Views/NoteWindow.xaml.cs#L86-L121) 中的 `JumpToSearchHit` 已实现基于 `EditorTextBox.Select()` + `GetRectFromCharacterIndex()` + 视口居中滚动的成熟算法，F3 跳跃可 100% 直接复用。
2. **失焦选区高亮保持已配置**：
   [`NoteWindow.xaml:276`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Views/NoteWindow.xaml#L276) 已启用：
   ```xml
   IsInactiveSelectionHighlightEnabled="True"
   SelectionBrush="#3399FF"
   ```
   当焦点转移至查找输入框时，正文 `TextBox` 的当前命中项依然保持高亮，不会消失。
3. **快捷键位干净**：
   [`NoteWindow.xaml.cs:160-187`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Views/NoteWindow.xaml.cs#L160-L187) 目前只占用了 `Ctrl+N`、`Ctrl+W`、`Ctrl+D`、`Ctrl+H`、`Ctrl+P`，`Ctrl+F`、`F3`、`Shift+F3`、`Esc` 均未占用。

### 2.3 核心技术陷阱与规避策略
- **陷阱：静态索引缓存失效（Text Mutation Invalidation）**：
  若预先计算全文命中索引列表 `List<int>`，用户一旦在正文中输入或删除一个字符，所有缓存的索引全部偏移错乱。
- **正解：无状态即时动态查找（Dynamic Search-on-Demand）**：
  不缓存绝对索引列表。每次按 `F3` / `Shift+F3` 时，直接以当前光标/选区起点（`SelectionStart`）为基准，在 `EditorTextBox.Text` 上执行瞬时 `IndexOf` / `LastIndexOf`。便签文本通常在几百至数千字符，单次字符串检索耗时 `< 0.01ms`，零内存开销，且天然免疫文本修改引起的越界崩溃。

---

## 三、 三套可行技术方案对比

| 评估维度 | 方案 A：嵌入式 Mini 查找条（推荐） | 方案 B：纯快捷键 + 主列表联动 | 方案 C：Adorner 全文底纹高亮 |
| :--- | :--- | :--- | :--- |
| **功能完整度** | ★★★★★ (支持自由输入、匹配计数、上下切换) | ★★★☆☆ (仅支持跳转与继承词) | ★★★★★ (视觉华丽但维护成本极高) |
| **可发现性 / 易用性** | ★★★★★ (标准 `Ctrl+F` 弹条心智模型) | ★★☆☆☆ (无界面引导，纯靠记忆) | ★★★★★ |
| **界面遮挡与尺寸影响** | 极小（高度仅 28px，可折叠） | 无（零 UI 改动） | 中（需叠加浮层与装饰层） |
| **开发工作量** | 约 0.5 ~ 1 个工作日 | 约 2 ~ 3 小时 | 3 ~ 5 个工作日 |
| **稳定性与维护风险** | **极低**（纯 WPF 原生控件组合） | **极低**（纯后台逻辑微调） | **高**（WPF 渲染重绘漂移深水区） |
| **综合推荐度** | **首选方案（生产级体验）** | **备选方案（轻量探索级）** | **不推荐（工程过度设计）** |

---

## 四、 详细方案设计

### 4.1 方案 A（推荐首选）：嵌入式 Mini 查找条（Inline Compact FindBar）

#### 4.1.1 交互规范与全键盘流程
1. **唤起查找**：
   - 在便签窗口内任意位置按 `Ctrl+F`（或正文选中文本后按 `Ctrl+F`，自动将选中词填入）；
   - 在顶部工具栏下方以平滑动画展开 28px 高度的 Mini 查找条，搜索框自动获得焦点；
2. **循环定位（环形回绕）**：
   - 按 `Enter` 或 `F3`：定位到下一个命中项（Next），到底部后自动回绕至文本开头；
   - 按 `Shift+Enter` 或 `Shift+F3`：定位到上一个命中项（Previous），到顶部后回绕至文本末尾；
   - 匹配计数器实时显示：如 `1/4` 或 `无结果`；
3. **退出查找**：
   - 按 `Esc` 或点击右侧 `✕` 按钮：收起查找条，焦点无缝交还给正文 `EditorTextBox`。
4. **主列表联动继承**：
   - 从 `NotesListWindow` 搜索跳转进入便签时，便签窗口自动记录主列表搜索词，并在状态栏或查找条中同步，按 `F3` 即可立即接续下一个。

#### 4.1.2 XAML 布局设计（位于 Row 0 与 Row 1 之间）
```xml
<!-- 插入于 NoteWindow.xaml 的 Row 0 工具栏与 Row 1 正文之间 -->
<Border x:Name="FindBar"
        Grid.Row="1"
        Height="28"
        Background="{Binding Color, Converter={StaticResource NoteColorToToolbarConverter}}"
        BorderBrush="{StaticResource Fluent.SubtleBorderBrush}"
        BorderThickness="0,0,0,1"
        Visibility="Collapsed"
        Padding="6,2">
    <Grid>
        <Grid.ColumnDefinitions>
            <!-- 搜索图标 -->
            <ColumnDefinition Width="Auto" />
            <!-- 输入框 -->
            <ColumnDefinition Width="*" />
            <!-- 结果计数 -->
            <ColumnDefinition Width="Auto" />
            <!-- 导航按钮组 (上一个/下一个) -->
            <ColumnDefinition Width="Auto" />
            <!-- 关闭按钮 -->
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>

        <ui:SymbolIcon Grid.Column="0" Symbol="Search16" FontSize="12" Foreground="{Binding Color, Converter={StaticResource NoteColorToSecondaryTextConverter}}" Margin="2,0,4,0"/>

        <TextBox x:Name="FindInputBox"
                 Grid.Column="1"
                 BorderThickness="0"
                 Background="Transparent"
                 VerticalAlignment="Center"
                 FontSize="12"
                 Foreground="{Binding Color, Converter={StaticResource NoteColorToTextConverter}}"
                 CaretBrush="{Binding Color, Converter={StaticResource NoteColorToTextConverter}}"
                 TextChanged="FindInputBox_TextChanged"
                 PreviewKeyDown="FindInputBox_PreviewKeyDown" />

        <TextBlock x:Name="FindCountText"
                   Grid.Column="2"
                   Text="0/0"
                   FontSize="11"
                   VerticalAlignment="Center"
                   Margin="4,0"
                   Foreground="{Binding Color, Converter={StaticResource NoteColorToSecondaryTextConverter}}" />

        <StackPanel Grid.Column="3" Orientation="Horizontal" VerticalAlignment="Center">
            <Button Style="{StaticResource StickyToolbarButtonStyle}" Width="20" Height="20" ToolTip="上一个 (Shift+F3)" Click="FindPrevious_Click">
                <ui:SymbolIcon Symbol="ChevronUp16" FontSize="11" Foreground="{Binding Color, Converter={StaticResource NoteColorToTextConverter}}" />
            </Button>
            <Button Style="{StaticResource StickyToolbarButtonStyle}" Width="20" Height="20" ToolTip="下一个 (F3)" Click="FindNext_Click">
                <ui:SymbolIcon Symbol="ChevronDown16" FontSize="11" Foreground="{Binding Color, Converter={StaticResource NoteColorToTextConverter}}" />
            </Button>
        </StackPanel>

        <Button Grid.Column="4" Style="{StaticResource StickyToolbarButtonStyle}" Width="20" Height="20" ToolTip="关闭 (Esc)" Click="CloseFindBar_Click" Margin="2,0,0,0">
            <ui:SymbolIcon Symbol="Dismiss16" FontSize="11" Foreground="{Binding Color, Converter={StaticResource NoteColorToTextConverter}}" />
        </Button>
    </Grid>
</Border>
```

#### 4.1.3 核心算法实现规范
```csharp
public partial class NoteWindow : Window
{
    private void FindNext()
    {
        var kw = FindInputBox.Text;
        if (string.IsNullOrEmpty(kw)) return;

        var text = EditorTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        int startPos = EditorTextBox.SelectionStart + EditorTextBox.SelectionLength;
        int nextIndex = text.IndexOf(kw, startPos, StringComparison.OrdinalIgnoreCase);

        // 环形回绕查找 (Wrap-around)
        if (nextIndex < 0)
        {
            nextIndex = text.IndexOf(kw, 0, StringComparison.OrdinalIgnoreCase);
        }

        if (nextIndex >= 0)
        {
            JumpToSearchHit(nextIndex, kw.Length);
            UpdateFindCount(nextIndex, kw);
        }
    }

    private void FindPrevious()
    {
        var kw = FindInputBox.Text;
        if (string.IsNullOrEmpty(kw)) return;

        var text = EditorTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        int startPos = EditorTextBox.SelectionStart > 0 ? EditorTextBox.SelectionStart - 1 : text.Length;
        int prevIndex = text.LastIndexOf(kw, startPos, StringComparison.OrdinalIgnoreCase);

        // 环形回绕查找 (Wrap-around)
        if (prevIndex < 0)
        {
            prevIndex = text.LastIndexOf(kw, text.Length, StringComparison.OrdinalIgnoreCase);
        }

        if (prevIndex >= 0)
        {
            JumpToSearchHit(prevIndex, kw.Length);
            UpdateFindCount(prevIndex, kw);
        }
    }
}
```

---

### 4.2 方案 B（备选极简）：纯快捷键 F3 联动（无独立查找栏）

#### 4.2.1 交互流程
1. 无常驻或滑出查找栏，便签界面维持 100% 纯粹；
2. 当用户在主列表检索并点击某卡片后，`WindowManager.NavigateToHit` 打开便签并传递 `currentSearchKeyword`；
3. 用户在便签内直接按 `F3`，直接在便签内跳到下一处，按 `Shift+F3` 跳上一处；
4. 底部微型辅助栏（Row 2）左侧的“字符数”位置临时闪烁展示“第 X 处 / 共 N 处” 2 秒，随后恢复字符数；
5. 若用户自行在便签内选中一段文字后按 `F3`，自动捕获该选中文本作为新的查找词向下跳跃。

#### 4.2.2 适用场景与评价
适合追求极度轻量、完全不需要查找框界面的场景；改动代码量少于 50 行，但缺少自由修改查找词的便利性。

---

### 4.3 方案 C（否决方案）：Adorner 全文底纹高亮

#### 4.3.1 实现机制
基于 `AdornerLayer` 挂载文本装饰层，重写 `OnRender`，在每次测量排版和滚动事件中计算所有命中字符的矩形，并绘制画刷矩形。

#### 4.3.2 淘汰原因
1. **排版与 DPI 漂移**：WPF 原生 `TextBox` 在多屏不同 DPI、窗口缩放、Ctrl+滚轮缩放字号以及快速平滑滚动时，Adorner 极易出现绘制滞后、残留断影与坐标偏差；
2. **性能与重绘开销**：长文本便签每次打字都会引发全文测量，造成输入顿挫；
3. **改用 RichTextBox 代价不可承受**：若彻底替换为 `RichTextBox`，会导致自动保存、字符统计、纯文本架构与轻量内存特性全面劣化。

---

## 五、 实施计划与影响面评估（针对方案 A）

### 5.1 模块与文件变更清单

| 文件 | 变更性质 | 说明 |
| :--- | :--- | :--- |
| [`NoteWindow.xaml`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Views/NoteWindow.xaml) | 布局微调 | 在正文区顶部插入 `FindBar` 容器（默认 `Collapsed`，高 28px） |
| [`NoteWindow.xaml.cs`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Views/NoteWindow.xaml.cs) | 逻辑补充 | 1. 扩展 `OnPreviewKeyDown` 拦截 `Ctrl+F`、`F3`、`Shift+F3`、`Esc`；<br>2. 增加 `FindNext` 与 `FindPrevious` 环形检索方法；<br>3. 焦点进出管理 |
| [`WindowManager.cs`](file:///c:/Home/Projects/StickyNotes/src/StickyNotes/Services/WindowManager.cs) | 联动扩展 | `NavigateToHit` 导航至便签时，将关键词传递给目标窗口暂存 |
| `NoteRepository` / `DB` / `Service` | **零改动** | 纯客户端 UI 交互，无需修改数据库与业务仓储 |

### 5.2 测试用例计划
1. **空内容/单字安全断言**：在空便签、单字便签中按 `F3`，断言不发生越界崩溃；
2. **环形回绕（Wrap-around）测试**：定位至最后一次命中后按 `F3`，断言平滑跳回第 1 处；
3. **边打字边查找（Text Mutation）测试**：在命中词中间插入新字符后按 `F3`，断言算法根据最新文本动态准确定位，无旧索引残留；
4. **焦点闭环测试**：按 `Esc` 退出查找条，断言正文光标位置正确且能立即正常打字。
