$ErrorActionPreference = 'Stop'
$source = 'E:\ModDev\TeraDeepOcean_LuaTraum_OLD\Assets\Content'
$destination = 'E:\ModDev\TeraDeepOcean_LuaTraum\ContentPackageBuilder'
$copied = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Import-Element($document, $sourceFile, $xpath, $parent) {
    $original = [xml][IO.File]::ReadAllText((Join-Path $source $sourceFile))
    $element = $original.SelectSingleNode($xpath)
    if (!$element) { throw "Missing XML element: $sourceFile $xpath" }
    $node = $document.ImportNode($element, $true)
    # 旧卡片中的该回调在旧项目也没有实现，删除失效的脚本标记。
    foreach ($hook in @($node.SelectNodes('.//LuaHook'))) { [void]$hook.ParentNode.RemoveChild($hook) }
    foreach ($attribute in @($node.SelectNodes('.//@texture | .//@file'))) {
        $value = $attribute.Value
        if ($value.StartsWith('%ModDir%/')) {
            $relative = $value.Substring(9)
        } elseif (!$value.StartsWith('Content/')) {
            $relative = Join-Path (Split-Path $sourceFile -Parent) $value
        } else {
            continue # 原版游戏资源
        }
        $asset = Join-Path $source $relative
        if (!(Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Missing RTS asset: $asset" }
        # 使用原文件真实大小写，发布到 Linux 时路径也能匹配。
        $actual = (Get-Item -LiteralPath $asset).FullName.Substring($source.Length + 1).Replace('\','/')
        $attribute.Value = '%ModDir%/' + $actual
        if ($copied.Add($actual)) {
            $target = Join-Path $destination $actual
            [void][IO.Directory]::CreateDirectory((Split-Path $target -Parent))
            Copy-Item -LiteralPath $asset -Destination $target
        }
    }
    [void]$parent.AppendChild($node)
}

$items = [xml]'<Items />'
$definitions = @(
    @('Content/Items/Equipment/Other/other.xml', 'tl_rts_command_device_test'),
    @('Content/Items/Sundrys/Sundrys.xml', 'idcardplus1B'),
    @('Content/Items/Equipment/Clothings/clothings.xml', 'var_leopard'),
    @('Content/Items/EnemyItems/EnemyWeapons.xml', 'TL_TL15_Enemy'),
    @('Content/Items/EnemyItems/EnemyWeapons.xml', 'TL14_magazine_Enemy'),
    @('Content/Items/Weapons/Guns/Magazine.xml', 'TL15_Ammo')
)
foreach ($entry in $definitions) {
    Import-Element $items $entry[0] "/*/*[@identifier='$($entry[1])']" $items.DocumentElement
}
$rtsDir = Join-Path $destination 'Content/RTS'
[void][IO.Directory]::CreateDirectory($rtsDir)
$items.Save((Join-Path $rtsDir 'RtsItems.xml'))

$afflictions = [xml]'<Afflictions />'
foreach ($identifier in @('last_B','stabilozineeffectTL_Pro')) {
    Import-Element $afflictions 'Content/Afflictions/Afflictions.xml' "/*/*[@identifier='$identifier']" $afflictions.DocumentElement
}
$afflictions.Save((Join-Path $rtsDir 'RtsAfflictions.xml'))

$npcs = [xml]'<npcsets><npcset identifier="TLnpc" /></npcsets>'
Import-Element $npcs 'Content/ModContent/NPCS/Npcs.xml' "//npcset[@identifier='TLnpc']/npc[@identifier='TLnpc2']" $npcs.DocumentElement.FirstChild
$npcs.Save((Join-Path $rtsDir 'RtsNpcs.xml'))
Write-Output "Imported 6 items, 2 afflictions, 1 NPC and $($copied.Count) assets."
