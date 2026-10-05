#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 cpio.gz initrd 里取出一条（或多条）同名条目的字节，落到本地或直接比对。

为什么先要有它再谈注入：注入前必须知道「包里现在跑的是哪一版」。2026-10-05 实测两次踩到：
① 用仓库重编的 gb.dex 直接覆盖部署包，差点把兄弟会话只在部署包里的修复倒回去；
② 部署包里的 /init 比仓库脚本多了 crc16 修复 —— 照仓库脚本重新生成 /init 就等于回滚别人的修复。
所以：**先 dump 比对，一致才敢换**（比对方式：sha1 相同，或本工具 --check 直接判）。

用法：python dump_initrd_entry.py <initrd.gz> <包内路径> [输出文件]
      只做比对：python dump_initrd_entry.py <initrd.gz> <包内路径> --check <本地文件>
"""
import gzip
import hashlib
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from replace_initrd_entry import parse  # noqa: E402  同一份解析器，别再写第二套


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    gz, want = sys.argv[1], sys.argv[2].lstrip("/")
    a3 = sys.argv[3] if len(sys.argv) > 3 else None
    with gzip.open(gz, "rb") as f:
        blob = f.read()
    found = [(n, d) for h, n, d in parse(blob) if n.lstrip("./") == want]
    if not found:
        # 查不到必须非零退出：静默返回会被读成"包里没有这个东西"
        print("查无此项: %s（包内条目 %d 个）" % (want, len(parse(blob))))
        sys.exit(1)
    for idx, (n, d) in enumerate(found):
        print("%s #%d  size=%d  sha1=%s" % (n, idx, len(d), hashlib.sha1(d).hexdigest()))
        if a3 == "--check":
            exp = open(sys.argv[4], "rb").read()
            print("  与 %s 相符=%s（期望 %dB）" % (sys.argv[4], d == exp, len(exp)))
        elif a3:
            name = a3 if len(found) == 1 else "%s.%d" % (a3, idx)
            with open(name, "wb") as w:
                w.write(d)
            print("  → %s" % name)


if __name__ == "__main__":
    main()
