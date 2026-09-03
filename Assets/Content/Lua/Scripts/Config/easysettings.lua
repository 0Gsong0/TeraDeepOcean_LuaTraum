local easySettings = {}
easySettings.Settings = {}

local function GetChildren(comp)
    local tbl = {}
    for child in comp.GetAllChildren() do
        table.insert(tbl, child)
    end
    return tbl
end

Hook.Patch("Barotrauma.GUI", "TogglePauseMenu", {}, function()
    if not GUI.GUI.PauseMenuOpen then return end

    local frame = GUI.GUI.PauseMenu
    local children = GetChildren(frame)
    if not children[2] then return end

    local innerChildren = GetChildren(children[2])
    if not innerChildren[1] then return end

    local list = innerChildren[1]

    for _, setting in pairs(easySettings.Settings) do
        local button = GUI.Button(
            GUI.RectTransform(Vector2(1, 0.1), list.RectTransform),
            setting.Name,
            GUI.Alignment.Center,
            "GUIButtonSmall"
        )

        button.OnClicked = function()
            setting.OnOpen(frame)
        end
    end
end, Hook.HookMethodType.After)

function easySettings.AddMenu(name, onOpen)
    table.insert(easySettings.Settings, {
        Name = name,
        OnOpen = onOpen
    })
end
--生成配置窗口底板
function easySettings.BasicFrame(parent, titleText, size)
    --整个窗口最外层框体
    local frame = GUI.Frame(
        GUI.RectTransform(size or Vector2(0.45, 0.75), parent.RectTransform, GUI.Anchor.Center),
        "GUIFrame"
    )
    --主布局容器
    local main = GUI.LayoutGroup(
        GUI.RectTransform(Vector2(0.96, 0.96), frame.RectTransform, GUI.Anchor.Center),
        false
    )
    --标题文本
    local title = GUI.TextBlock(
        GUI.RectTransform(Vector2(1, 0.08), main.RectTransform),
        titleText or "Config",
        nil,
        GUI.GUIStyle.LargeFont,
        GUI.Alignment.Center
    )
    --中间用来放配置项的列表
    local list = GUI.ListBox(
        GUI.RectTransform(Vector2(1, 0.82), main.RectTransform)
    )
    --底部按钮容器
    local buttonRow = GUI.LayoutGroup(
        GUI.RectTransform(Vector2(1, 0.08), main.RectTransform),
        true
    )
    buttonRow.RelativeSpacing = 0.02

    return {
        Frame = frame,
        Main = main,
        Title = title,
        List = list,
        ButtonRow = buttonRow
    }
end

return easySettings