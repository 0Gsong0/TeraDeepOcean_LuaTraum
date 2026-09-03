TLHF = {}
--GetAff
function TLHF.HasAfflictionLimb(character,identifier,limb,defaultvalue)
    --local limb = character.AnimController.GetLimb(limbtype)
    if limb==nil then return false end
    local aff = character.CharacterHealth.GetAffliction(identifier,limb)
    local res = false
    if(aff~=nil) then
        res = aff.Strength >= (defaultvalue or 0.1)
    end
    return res
end
function TLHF.HasAffliction(character,identifier,defaultvalue)
    defaultvalue = defaultvalue or 0.1
    if character == nil or character.CharacterHealth == nil then return defaultvalue end
    local aff = character.CharacterHealth.GetAffliction(identifier)
    if aff ~= nil then 
        return aff.Strength >= defaultvalue
    end
    return false
end
function TLHF.GetAfflictionLimbStrength(character, identifier, limb, defaultvalue)
    defaultvalue = defaultvalue or 0
    if character == nil or limb == nil or character.CharacterHealth == nil then return defaultvalue end
    local aff = character.CharacterHealth.GetAffliction(identifier,limb)
    if aff ~= nil then 
        return aff.Strength
    end
    return defaultvalue
end
function TLHF.GetAfflictionStrength(character, identifier, defaultvalue)
    defaultvalue = defaultvalue or 0
    if character == nil or character.CharacterHealth == nil then return defaultvalue end
    
    local aff = character.CharacterHealth.GetAffliction(identifier)
    if aff ~= nil then 
        return aff.Strength
    end
    return defaultvalue
end
--AddAff
function TLHF.AddAfflictionLimb(character,identifier,limb,strength)
    if character==nil or limb== nil then return end

    local oldAmount = TLHF.GetAfflictionLimbStrength(character,identifier,limb)
    local newAmount = oldAmount + strength
    TLHF.SetAfflictionLimb(character,identifier,limb,newAmount)
end
function TLHF.AddAffliction(character,identifier,strength)
    if character == nil or character.CharacterHealth == nil then return end
    local limb = character.AnimController.MainLimb
    local oldAmount = TLHF.GetAfflictionStrength(character,identifier)
    local newAmount = oldAmount + strength
    TLHF.SetAfflictionLimb(character, identifier, limb, newAmount)
end
function TLHF.SetAfflictionLimb(character,identifier,limb,strength,fromCharacter)
    if strength == 0 then strength = -1000 end
    local prefab = AfflictionPrefab.Prefabs[identifier]
    local resistance = 0
    if prefab then
        resistance = character.CharacterHealth.GetResistance(prefab,limb.type)
    end
    if resistance >= 1 then return end
    strength = strength*character.CharacterHealth.MaxVitality/100/(1-resistance)
    local affliction = prefab.Instantiate(strength,fromCharacter)
    character.CharacterHealth.ApplyAffliction(limb,affliction,false)
end
function TLHF.SetAffliction(character, identifier, strength, fromCharacter)
    if character == nil or character.AnimController == nil then return end
    local limb = character.AnimController.MainLimb
    if limb == nil then return end
    TLHF.SetAfflictionLimb(character, identifier, limb, strength, fromCharacter)
end
function TLHF.GiveAfflictionToAll(identifier)
    for character in Character.CharacterList do
        if character ~= nil and not character.Removed and character.IsHuman and not character.IsDead then
            TLHF.AddAffliction(character, identifier, 100)
        end
    end
end
function TLHF.ClearAllTLCybAff(character,limb)
    if character == nil or limb == nil then return end
    TLHF.SetAfflictionLimb(character, "TLCyb_CivilianArm_Init", limb,0)
    TLHF.SetAfflictionLimb(character, "TLCyb_CivilianLeg_Init", limb,0)
    TLHF.SetAfflictionLimb(character, "TLCyb_StructuralDamage", limb,0)
    TLHF.SetAfflictionLimb(character, "TLCyb_ServoDamage", limb,0)
    TLHF.SetAfflictionLimb(character, "TLCyb_CircuitDamage", limb,0)
    TLHF.SetAfflictionLimb(character, "TLCyb_SystemDamage", limb,0)
end
--Item
function TLHF.PlaySound(item,character)
    TLHF.GiveItem(item,character,true)
end
function TLHF.GiveItem(item,character,drop)
    drop = drop or false
    local Item = ItemPrefab.GetItemPrefab(item)
    if drop then
        Entity.Spawner.AddItemToSpawnQueue(Item, character.WorldPosition)
    else
        Entity.Spawner.AddItemToSpawnQueue(Item, character.Inventory, nil, nil, nil)
    end
end
function TLHF.RemoveItem(item)
    if item == nil or item.Removed then return end
    if SERVER then
        Entity.Spawner.AddEntityToRemoveQueue(item)
    else
        item.Remove()
    end
end
function TLHF.DamageItem(item, amount)
    if not item or item.Removed then return end
    if amount <= 0 then return end

    item.Condition = item.Condition - amount

    if item.Condition <= 0 then
        item.Condition = 0
        TLHF.RemoveItem(item)
    end
end
--是否应该强制失去意识
function TLHF.ShouldBeUnconscious(character)
    if not character or character.Removed or character.IsDead or not character.IsHuman then
        return false
    end
    return TLHF.HasAffliction(character, "TL_StunState", 0.1)
end
--肢体归一化
function TLHF.NormalizeLimbType(limbtype)
    if limbtype == LimbType.LeftForearm or limbtype == LimbType.LeftHand then
        return LimbType.LeftArm
    end
    if limbtype == LimbType.RightForearm or limbtype == LimbType.RightHand then
        return LimbType.RightArm
    end
    if limbtype == LimbType.LeftThigh or limbtype == LimbType.LeftFoot then
        return LimbType.LeftLeg
    end
    if limbtype == LimbType.RightThigh or limbtype == LimbType.RightFoot then
        return LimbType.RightLeg
    end
    if limbtype == LimbType.Waist then
        return LimbType.Torso
    end
    return limbtype
end
function TLHF.GetLimb(character,limbType)
    return character.AnimController.GetLimb(limbType)
end
--强制锁手
function TLHF.ForceArmLock(character, identifier)
    if not character or not character.Inventory then return end

    local item = character.Inventory.FindItemByIdentifier(identifier, false)
    if item ~= nil then return end

    TLHF.GiveItem(identifier,character)
end
--丢掉手中的物品
function TLHF.DropItemsFromLockedArms(character, leftShouldLock, rightShouldLock)
    if not character then return end

    if leftShouldLock then
        local leftItem = TLHF.GetItemInLeftHand(character)
        if leftItem ~= nil and leftItem.Prefab.Identifier.Value ~= "armlock2" then
            leftItem.Drop(character)
        end
    end

    if rightShouldLock then
        local rightItem = TLHF.GetItemInRightHand(character)
        print(character.Inventory.GetItemAt(1).Prefab.name)
        if rightItem ~= nil and rightItem.Prefab.Identifier.Value ~= "armlock1" then
            rightItem.Drop(character)
        end
    end
end
function TLHF.GetItemInRightHand(character)
	return TLHF.GetCharacterInventorySlot(character, 6)
end
function TLHF.GetItemInLeftHand(character)
	return TLHF.GetCharacterInventorySlot(character, 5)
end
function TLHF.GetOuterWear(character)
	return TLHF.GetCharacterInventorySlot(character, 4)
end
function TLHF.GetInnerWear(character)
	return TLHF.GetCharacterInventorySlot(character, 3)
end
function TLHF.GetHeadWear(character)
	return TLHF.GetCharacterInventorySlot(character, 2)
end
function TLHF.GetCharacterInventorySlot(character, slot)
	return character.Inventory.GetItemAt(slot)
end
--Other
function TLHF.GameIsPaused()
    if SERVER then
        return false
    end

    return Game.Paused
end
function TLHF.HasNeurotraumaLoaded()
    if NT ~= nil then
        if AfflictionPrefab.Prefabs["cerebralhypoxia"] ~= nil then
            return true
        end
    end
    return false
end
function TLHF.HasNeurotraumaCybLoaded()
    if NT ~= nil then
        if NTCyb == nil then return false end
        if AfflictionPrefab.Prefabs["ntc_cyberlimb"] ~= nil then
            return true
        end
    end
    return false
end
function  TLHF.StartsWith(str,start)
    if str == nil or start == nil then
        return false
    end
    return string.sub(str, 1, string.len(start)) == start
end
function TLHF.GetSkillLevel(character, skillIdentifier)
    if character == nil then return 0 end
    return character.GetSkillLevel(Identifier(skillIdentifier))
end

function TLHF.ShowDialogToAll(title, text,client)
    if SERVER then
        Game.SendDirectChatMessage(title, text, nil, 7, client)
    else
        GUI.MessageBox(title, text)
    end
end
