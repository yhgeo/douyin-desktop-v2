# -*- coding: utf-8 -*-
"""生成 a_bogus 的 golden 测试向量。

用途：Rust 移植 `dy-sign` 时，用这些向量做逐字节比对。没有这组基准，
移植是否「碰巧能过」和「真的对」无法区分。

原理：`ABogusPureSigner(fixed=True)` 把时间、随机数、指纹全部钉死，
同一个 (query, body, host, ua, counter) 必然产出同一个签名。

用法：
    python gen_abogus_vectors.py --spider-dir /path/to/DouYin_Spider \
        --out abogus_vectors.json

依赖：只需要 DouYin_Spider 的 utils/（ab_pure.py 会 import sm3 与 fingerprint）。
"""
import argparse
import json
import os
import sys
from datetime import datetime, timezone

# 真实推荐流参数（来自 DouyinAPI.get_feed 的抓包，顺序敏感，不要重排）
FEED_QUERY = (
    "device_platform=webapp&aid=6383&channel=channel_pc_web&module_id=3003101"
    "&count=20&filterGids=&presented_ids=&refresh_index=2&refer_id=&refer_type=10"
    "&awemePcRecRawData=%7B%22is_client%22%3Afalse%7D&Seo-Flag=0"
    "&install_time=1715480185&pc_client_type=1&update_version_code=170400"
    "&version_code=170400&version_name=17.4.0&cookie_enabled=true"
    "&screen_width=2560&screen_height=1440&browser_language=zh-CN"
    "&browser_platform=Win32&browser_name=Chrome&browser_version=151.0.0.0"
    "&browser_online=true&engine_name=Blink&engine_version=151.0.0.0"
    "&os_name=Windows&os_version=10&cpu_core_num=16&device_memory=8"
    "&platform=PC&downlink=10&effective_type=4g&round_trip_time=100"
    "&webid=7512345678901234567&msToken=FAKE_MS_TOKEN_FOR_TEST"
)

CHROME_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36"
)


def z149_bucket(counter):
    """与 utils/ab_pure.py 的 z149_counter_bucket 保持一致，仅用于用例命名。

    注意：sign_query 内部先 ``self.counter += 1`` 再分桶，所以实际参与计算的是
    ``counter + 1``。这里显式加回来，否则用例名会骗人。
    """
    c = counter + 1
    if c > 10745:
        return 3
    if c > 1283:
        return 4
    if c > 139:
        return 5
    return 6


def build_cases():
    """返回 [(host, query, body, ua, counter, note), ...]"""
    cases = []

    # 1) 主站真实 feed 参数：counter 覆盖全部四个桶及其边界。
    #    实际分桶用的是 counter+1，故边界取在 138/139、1282/1283、10744/10745。
    for counter in [138, 139, 1282, 1283, 10744, 10745, 2, 500, 5000, 50000]:
        cases.append(("www.douyin.com", FEED_QUERY, "", None, counter,
                      f"feed 真实参数 / counter={counter}（桶{z149_bucket(counter)}）"))

    # 2) 边界 query：空、单参数、超长、含中文与特殊字符
    edge_queries = [
        "",
        "a=1",
        "a=1&b=2&c=3",
        "q=" + "x" * 512,
        "keyword=%E7%BE%8E%E9%A3%9F&count=10",          # 中文（已编码）
        "keyword=美食&count=10",                          # 中文（未编码）
        "a=1&a=2&a=3",                                    # 重复键
        "empty=&novalue&x=1",
        "a=%2B%2F%3D&b=%20&c=%25",                        # 转义字符
    ]
    for i, q in enumerate(edge_queries):
        cases.append(("www.douyin.com", q, "", None, 2 + i,
                      f"边界 query #{i}"))

    # 3) 不同 host：aid / page_id / sdk_minor 随子域变化。
    #    主站已由第 1 组覆盖，这里只测另外两个子域，避免同桶重复。
    for host, c in [("live.douyin.com", 200), ("login.douyin.com", 200)]:
        cases.append((host, "aid=6383&room_id=123456", "", None, c, f"host={host}"))
    for host, c in [("live.douyin.com", 1500), ("login.douyin.com", 1500)]:
        cases.append((host, FEED_QUERY, "", None, c, f"host={host} + feed"))

    # 4) 带 body（POST 签名路径，h2 参与）
    for i, body in enumerate([
        "aweme_id=7695655459017882341&text=hello",
        '{"aweme_id":"7695655459017882341","action":"digg"}',
        "text=%E4%BD%A0%E5%A5%BD",
        "a=1",
        "",
    ]):
        cases.append(("www.douyin.com", "device_platform=webapp&aid=6383",
                      body, None, 3 + i, f"body #{i}"))

    # 5) 换 UA（UA 参与 RC4 与 env_flags，必须单独覆盖）
    for i, c in enumerate([300, 1300, 11000]):
        cases.append(("www.douyin.com", FEED_QUERY, "", CHROME_UA, c,
                      f"Chrome UA #{i} / counter={c}（桶{z149_bucket(c)}）"))
    cases.append(("www.douyin.com", "a=1", "", CHROME_UA, 400, "Chrome UA 短 query"))

    return cases


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--spider-dir", required=True,
                    help="DouYin_Spider 仓库根目录（需含 utils/）")
    ap.add_argument("--out", default="abogus_vectors.json")
    args = ap.parse_args()

    sys.path.insert(0, os.path.abspath(args.spider_dir))
    from utils.ab_pure import ABogusPureSigner, FIXED_NOW, FIXED_RAND, FIREFOX_UA

    vectors = []
    for idx, (host, query, body, ua, counter, note) in enumerate(build_cases(), 1):
        # 每次新建 signer：counter / rand 游标都从头开始，保证可复现
        kwargs = {"fixed": True, "fixed_counter": counter}
        if ua is not None:
            kwargs["ua"] = ua
        signer = ABogusPureSigner(**kwargs)
        sig = signer.sign_query(query, body, host=host)

        vectors.append({
            "id": f"v{idx:03d}",
            "note": note,
            "host": host,
            "query": query,
            "body": body,
            "ua": ua or FIREFOX_UA,
            "now_ms": FIXED_NOW,
            "rand": FIXED_RAND,
            "counter": counter,
            "expected": sig,
        })

    payload = {
        "meta": {
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "source": "cv-cat/DouYin_Spider utils/ab_pure.py",
            "signer": "ABogusPureSigner(fixed=True)",
            "note": "now_ms/rand/counter 全部钉死，同一输入必然产出同一签名",
            "count": len(vectors),
        },
        "vectors": vectors,
    }

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)

    print(f"已生成 {len(vectors)} 组向量 -> {args.out}")
    for v in vectors[:3]:
        print(f"  {v['id']} {v['note']}: {v['expected'][:40]}...")
    # 同桶内签名必然相同，所以唯一数少于总数是正常的；这里只确认覆盖了 4 个桶
    buckets = sorted({v["note"].split("桶")[-1].rstrip("）") for v in vectors
                      if "桶" in v["note"]})
    print(f"覆盖桶: {buckets}  唯一签名 {len({v['expected'] for v in vectors})}/{len(vectors)}")


if __name__ == "__main__":
    main()
