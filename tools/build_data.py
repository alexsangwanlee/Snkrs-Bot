"""원랜디 데이터 묶음(Data/ord-data.json, Data/images) 생성기.

입력: asmond-lab/onepiece-random-defense-overlay 저장소의 Data 폴더 (MIT).
  python3 tools/build_data.py <overlay-repo>/Data

조합식 우선순위: ORDR 2.323 맵 원본(war3map.j 추출) > TMO 42479 조합도우미.
유카 = 클리어 시점 필드 유닛 카운트 (TMO 클리어 기록의 n 필드).
"""
import collections
import json
import re
import shutil
import sys
from pathlib import Path

SRC = Path(sys.argv[1] if len(sys.argv) > 1 else "../onepiece-random-defense-overlay/Data")
OUT = Path(__file__).resolve().parent.parent / "Data"
MAP_VERSION = "2.323"
MAP_TAG = MAP_VERSION.replace(".", "")

# 강화·변신으로 rawcode만 바뀌는 같은 유닛 (참고 저장소 RawcodeAliases와 동일).
# 클리어 통계와 메모리 인식 모두 대표 코드로 합친다.
ALIASES = {"G90H": "H90H", "D90H": "E90H", "1B0H": "790H", "390H": "190H",
           "TB0H": "F90H", "OB0H": "LB0H", "DA0h": "M70h", "MB0h": "E40h"}
# 니카(뱀초)는 조합 트리만 다르고 인게임 코드는 KB0H 하나.
STATS_ALIASES = {"KB0H_": "KB0H"}
# 맵 조합식의 선택 재료: 보유한 후보 중 아무거나 1기 (참고 저장소 RecipeWildcards).
WILDCARDS = {"T80H": ("루피 또는 스네이크맨 초월", ["990H", "2B0H"]),
             "NA0h": ("세라핌 아무거나", ["0A0h", "1A0h", "3A0h", "Y90h"])}

CONDITION_LABEL = {
    "KING": "상위 유닛 수 제한", "UBAN": "동시 보유 금지 유닛 있음", "PICK": "랜덤전용 유닛 1기 선택 소모",
    "GREN": "그린블러드 필요", "RDUN": "랜덤 유닛 필요", "SPEC": "특수 조건", "IFCT": "조건부 재료",
    "CHCT": "선택 조건", "SKPT": "특성포인트 필요", "RMAX": "최대 횟수 제한",
}


def load(name):
    return json.loads((SRC / name).read_text(encoding="utf-8"))


def rev(code):  # 맵 원본 id(h00K) -> 메모리/TMO rawcode(K00h)
    return code[::-1]


def split_tier(tier):
    tier = tier.strip()
    if tier.startswith("해적선"):
        return "해적선", ""
    if "[" in tier:
        grade, role = tier.split("[", 1)
        return grade.strip(), role.rstrip("]").strip()
    return tier, ""


# 1) TMO 42479 카탈로그 + 신규 유닛 + 조합식 오버라이드
catalog = {u["rawcode"]: u for u in load("tmo-unit-catalog.json")["units"]}
for u in load("tmo-unit-additions-42479.json")["units"]:
    catalog[u["rawcode"]] = u
for line in (SRC / "tmo-recipe-overrides-42479.txt").read_text(encoding="utf-8").splitlines():
    if line.strip() and not line.startswith("#"):
        code, recipe = line.split("=", 1)
        catalog[code]["recipe"] = [{"id": p.split(":")[0], "count": int(p.split(":")[1])}
                                   for p in recipe.split(",")]
# 2) TMO 43747 가이드: 등급·능력치 교정
# OX 조합기처럼 이름 옆에 붙는 짧은 능력 요약은 가이드 이름의 괄호 안 표기를 쓴다.
memos = {}
for g in load("tmo-guide-43747.json")["unitOverrides"]:
    if g["rawcode"] in catalog:
        catalog[g["rawcode"]].update(tier=g["tier"], abilities=g["abilities"],
                                     description=g.get("description", ""))
        found = re.findall(r"\(([^()]*)\)", g.get("guideName", ""))
        if found:
            memos[g["rawcode"]] = found[-1].strip()

SHORT = {"방어력 감소": "방깎", "이동속도 감소": "이감", "발동이동속도 감소": "발동이감", "공격속도 증가": "공증",
         "공격력 증가": "공업", "단일방어력 감소": "단일방깎", "마법방어력 감소": "마방깎", "발동방어력 감소": "발동방깎",
         "폭발형 데미지 증폭": "폭뎀증", "마법데미지 증폭": "마뎀증", "모든피해증가": "모피증", "보스 잡기": "보잡",
         "광폭화": "광폭", "유닛삭제": "삭제", "아머브레이크": "암브", "처형": "처형"}


def memo_from(abilities):
    parts = []
    for key, value in abilities.items():
        if key == "스턴" and isinstance(value, (int, float)):
            parts.append(f"{value:g}스턴")
        elif key in SHORT and isinstance(value, (int, float)) and not isinstance(value, bool):
            parts.append(f"{SHORT[key]}{value:g}")
        elif key in SHORT and value in (True, "true"):
            parts.append(SHORT[key])
    return " ".join(parts[:4])

# 이름 보강용 (맵 원본 이름표, 능력치 표, 단축키 표)
extra_names = {}
board = json.loads((SRC.parent / "docs/analysis-2320/utility-board-formulas.json").read_text(encoding="utf-8"))


def walk(o):
    if isinstance(o, dict):
        if isinstance(o.get("unit"), str) and isinstance(o.get("name"), str):
            text = re.sub(r"\|[cC][0-9a-fA-F]{8}|\|[rR]", "", o["name"])
            name, _, tier = text.partition(" - ")
            extra_names.setdefault(rev(o["unit"]), (name.strip(), tier.strip() or "기타"))
        for v in o.values():
            walk(v)
    elif isinstance(o, list):
        for v in o:
            walk(v)


walk(board)
for u in load("tmo-hand-stats-48784.json")["units"]:
    extra_names[u["rawcode"]] = (u["name"], u["tier"])
for e in load("tmo-combine-hotkeys.json")["entries"]:
    name, _, tier = (e["resultName"] or e["result"]).partition(" - ")
    extra_names.setdefault(e["result"], (name, tier))

units = {}


def ensure(code, name=None, tier=None):
    if code not in units:
        guess_name, guess_tier = extra_names.get(code, (f"미확인({code})", "기타"))
        grade, role = split_tier(tier or guess_tier)
        units[code] = {"id": code, "name": name or guess_name, "grade": grade, "role": role, "recipe": [],
                       "gold": 0, "lumber": 0, "notes": [], "key": "", "hosts": [], "commands": [],
                       "abilities": {}, "description": "", "recipeSource": "", "anyOf": [], "memo": "",
                       "targeted": False}
    return units[code]


def is_unit(code):
    u = catalog.get(code)
    return u is not None and u["tier"] not in ("자원", "아이템")


def note_for(code, count):
    if code == "POINT":
        return f"특성포인트 {count}"
    if code == "seraphim_any":
        return "세라핌 아무거나 1"
    u = catalog.get(code)
    if u is None:
        return f"맵 전용 아이템 {count}개 필요"
    return f"{u['name']} {count}" + (" (아이템)" if u["tier"] == "아이템" else "")


for code, c in catalog.items():
    if code in ALIASES or split_tier(c["tier"])[0] in ("자원", "아이템"):
        continue
    u = ensure(code, c["name"], c["tier"])
    u.update(abilities=c.get("abilities", {}), description=c.get("description", ""),
             memo=memos.get(code) or memo_from(c.get("abilities", {})))
    for i in c.get("recipe", []):
        if i["id"] == "GOLD":
            u["gold"] += i["count"]
        elif i["id"] == "LUMBER":
            u["lumber"] += i["count"]
        elif i["id"] == "seraphim_any":
            u["recipe"].append({"id": "NA0h", "count": i["count"]})
        elif is_unit(i["id"]):
            u["recipe"].append({"id": ALIASES.get(i["id"], i["id"]), "count": i["count"]})
        else:
            u["notes"].append(note_for(i["id"], i["count"]))
    u["recipeSource"] = "TMO 42479" if u["recipe"] else ""

# 3) 2.323 맵 원본 조합식이 TMO 조합식을 덮어쓴다
for add in load(f"map-unit-additions-{MAP_TAG}.json")["units"]:
    ensure(add["rawcode"], add["name"], add["tier"])
recipes = load(f"map-recipes-{MAP_TAG}.json")
active = {c["outputId"]: c["recipeId"] for c in recipes["activeChoices"]}
for r in recipes["recipes"]:
    out_id = r["output"]["id"]
    code = rev(out_id)
    if (out_id in active and active[out_id] != r["recipeId"]) or code in ALIASES:
        continue
    ing, gold, lumber, notes, targeted = collections.Counter(), 0, 0, [], False
    for c in r["conditions"]:
        kind = c["kind"]
        if kind == "UNIT":
            ing[ALIASES.get(rev(c["id"]), rev(c["id"]))] += c["count"]
        elif kind == "WOOD":
            lumber += c["count"]
        elif kind == "GOLD":
            gold += c["count"]
        elif kind == "ITEM":
            notes.append(note_for(rev(c["id"]), c["count"]))
        elif kind in CONDITION_LABEL:
            targeted |= kind == "PICK"  # 대상 유닛을 클릭해 시전해야 하는 조합
            label = c.get("label") or CONDITION_LABEL[kind]
            if label not in notes:
                notes.append(label)
    ensure(code).update(recipe=[{"id": k, "count": v} for k, v in ing.items()], gold=gold, lumber=lumber,
                        notes=notes, targeted=targeted, recipeSource=f"맵 {MAP_VERSION}")
for u in list(units.values()):
    for i in u["recipe"]:
        ensure(i["id"])
for code, (name, options) in WILDCARDS.items():
    ensure(code).update(name=name, grade="선택 재료", role="", anyOf=options)

# 4) 조합 단축키 (2.323 우선, 없으면 2.314 TMO 표) + 채팅 조합 명령
for e in load("tmo-combine-hotkeys.json")["entries"]:
    if e["result"] in units:
        units[e["result"]]["key"] = e["key"]
for e in load(f"map-combine-hotkeys-{MAP_TAG}.json")["entries"]:
    if e["result"] in units:
        units[e["result"]].update(key=e["key"], hosts=[h["rawcode"] for h in e["hosts"]])
for line in (SRC / f"commands-{MAP_TAG}.txt").read_text(encoding="utf-8").splitlines():
    if line.strip() and not line.startswith("#") and "=" in line:
        code, aliases = line.split("=", 1)
        if code in units:
            units[code]["commands"] = [a.strip() for a in aliases.split("|") if a.strip()]

# 5) 클리어 기록 (유카 = n)
clears = load("tmo-clear-samples.json")
samples = []
for s in clears["samples"]:
    codes = sorted({ALIASES.get(u["c"], u["c"]) for u in s["u"]})
    for c in codes:
        ensure(c)
    samples.append([s["d"], s["n"], codes])

# 6) 이미지 (rawcode_XXXX.png -> XXXX.png)
shutil.rmtree(OUT / "images", ignore_errors=True)
(OUT / "images").mkdir(parents=True)
named = {u["id"]: c for u in load("game-data.demo.json")["units"] for c in u.get("rawcodes", [])}
for png in (SRC / "images").glob("*.png"):
    code = png.stem[len("rawcode_"):] if png.stem.startswith("rawcode_") else named.get(png.stem)
    if code in units:
        shutil.copy(png, OUT / "images" / f"{code}.png")

doc = {
    "schemaVersion": 1,
    "mapVersion": MAP_VERSION,
    "sources": [
        "https://github.com/asmond-lab/onepiece-random-defense-overlay (MIT) Data/",
        "https://tmo.gg/g/ord/build-helper/42479",
        clears["source"],
    ],
    "aliases": {**ALIASES, **STATS_ALIASES},
    "units": sorted(units.values(), key=lambda u: u["id"]),
    "clears": {"capturedAt": clears["capturedAt"], "sinceDays": clears["sinceDays"], "samples": samples},
}
(OUT / "ord-data.json").write_text(json.dumps(doc, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

missing = [(u["id"], i["id"]) for u in units.values() for i in u["recipe"] if i["id"] not in units]
assert not missing, missing
print(f"units={len(units)} recipes={sum(1 for u in units.values() if u['recipe'])} "
      f"keys={sum(1 for u in units.values() if u['key'])} "
      f"commands={sum(1 for u in units.values() if u['commands'])} samples={len(samples)} "
      f"images={len(list((OUT / 'images').glob('*.png')))} "
      f"unnamed={[c for c, u in units.items() if u['name'].startswith('미확인')]}")
