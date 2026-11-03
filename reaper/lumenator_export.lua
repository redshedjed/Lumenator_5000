-- Lumenator 5000: keeps the open REAPER project's chart in a file for `Lumenator --reaper`, including edits
-- you haven't saved. Read-only: it changes nothing in the project. Runs until you terminate it (run it again
-- and choose to terminate it) or close REAPER.
local folder = os.getenv("LOCALAPPDATA") .. "\\Lumenator"
local path = folder .. "\\reaper_chart.json"

-- VENUE and BEAT whole; instruments, just Expert gems and the drums' tom markers
local WHOLE = { VENUE = true, BEAT = true }
local GEMS = { ["PART GUITAR"] = true, ["PART BASS"] = true, ["PART KEYS"] = true, ["PART DRUMS"] = true }
local TRACK_NAME_EVENT = 3
local REFRESH_S = 2            -- rewritten this often even with no change, which tells Lumenator we're running
local HASH_CHECK_S = 0.25

local function quote(s)
  s = s:gsub("\\", "\\\\"):gsub('"', '\\"'):gsub("%c", function(c) return string.format("\\u%04x", c:byte()) end)
  return '"' .. s .. '"'
end

local function trackName(track)
  local _, name = reaper.GetSetMediaTrackInfo_String(track, "P_NAME", "", false)
  return name
end

local function isGem(pitch)
  return (pitch >= 96 and pitch <= 100) or (pitch >= 110 and pitch <= 112)
end

local function readTake(take, name, out)
  local _, notes, _, texts = reaper.MIDI_CountEvts(take)
  for k = 0, notes - 1 do
    local _, _, muted, startPpq, endPpq, _, pitch = reaper.MIDI_GetNote(take, k)
    if not muted and (WHOLE[name] or isGem(pitch)) then
      out[#out + 1] = string.format('{"track":%s,"t":%.4f,"end":%.4f,"note":%d}', quote(name),
        reaper.MIDI_GetProjTimeFromPPQPos(take, startPpq), reaper.MIDI_GetProjTimeFromPPQPos(take, endPpq), pitch)
    end
  end
  if name == "VENUE" then
    for k = 0, texts - 1 do
      local _, _, muted, ppq, kind, msg = reaper.MIDI_GetTextSysexEvt(take, k, true, true, 0, 0, "")
      if not muted and kind >= 1 and kind <= 15 and kind ~= TRACK_NAME_EVENT then
        out[#out + 1] = string.format('{"track":%s,"t":%.4f,"text":%s}', quote(name),
          reaper.MIDI_GetProjTimeFromPPQPos(take, ppq), quote(msg))
      end
    end
  end
end

local function tempoMap()
  local points = {}
  for i = 0, reaper.CountTempoTimeSigMarkers(0) - 1 do
    local _, at, _, _, bpm = reaper.GetTempoTimeSigMarker(0, i)
    points[#points + 1] = string.format("[%.4f,%.4f]", at, bpm)
  end
  if #points == 0 or not points[1]:find("^%[0%.0000,") then
    -- before the first marker: the project's own tempo
    table.insert(points, 1, string.format("[0.0000,%.4f]", reaper.GetProjectTimeSignature2(0)))
  end
  return table.concat(points, ",")
end

local function export()
  local out = {}
  for i = 0, reaper.CountTracks(0) - 1 do
    local track = reaper.GetTrack(0, i)
    local name = trackName(track)
    if WHOLE[name] or GEMS[name] then
      for j = 0, reaper.CountTrackMediaItems(track) - 1 do
        local take = reaper.GetActiveTake(reaper.GetTrackMediaItem(track, j))
        if take and reaper.TakeIsMIDI(take) then
          readTake(take, name, out)
        end
      end
    end
  end
  local _, project = reaper.EnumProjects(-1)
  local loopStart, loopEnd = reaper.GetSet_LoopTimeRange2(0, false, true, 0, 0, false)
  local json = string.format('{"project":%s,"repeat":%d,"loop":[%.4f,%.4f],"tempo":[%s],"events":[\n%s\n]}\n',
    quote(project or ""), reaper.GetSetRepeat(-1), loopStart, loopEnd, tempoMap(), table.concat(out, ",\n"))

  -- written whole, then swapped in, so Lumenator never reads half of it
  local tmp = path .. ".tmp"
  local f = io.open(tmp, "w")
  if not f then
    reaper.ShowConsoleMsg("Lumenator: can't write to " .. tmp .. "\n")
    return
  end
  f:write(json)
  f:close()
  os.remove(path)
  os.rename(tmp, path)
end

-- two ways of noticing an edit: REAPER's project change counter, and a fingerprint of the MIDI itself
local function midiHash()
  local parts = {}
  for i = 0, reaper.CountTracks(0) - 1 do
    local track = reaper.GetTrack(0, i)
    local name = trackName(track)
    if WHOLE[name] or GEMS[name] then
      local _, hash = reaper.MIDI_GetTrackHash(track, false, "")
      parts[#parts + 1] = hash
    end
  end
  return table.concat(parts, "|")
end

local lastChange, lastHash, lastHashCheck, lastExport = -1, nil, 0, 0

local function loop()
  local now = reaper.time_precise()
  local due = now - lastExport >= REFRESH_S
  local change = reaper.GetProjectStateChangeCount(0)
  if change ~= lastChange then
    lastChange = change
    due = true
  end
  if now - lastHashCheck >= HASH_CHECK_S then
    lastHashCheck = now
    local hash = midiHash()
    if hash ~= lastHash then
      lastHash = hash
      due = true
    end
  end
  if due then
    lastExport = now
    export()
  end
  reaper.defer(loop)
end

reaper.RecursiveCreateDirectory(folder, 0)
reaper.ShowConsoleMsg("Lumenator: sharing this project's chart. Now run: Lumenator --reaper\n")
loop()
