#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一次过替换 cpio.gz(newc) initrd 里**已存在**的多个条目。

为什么不用 replace_initrd_entry.py 跑三遍：每跑一遍都要把 ~1GB 解压再重压（实测 ~100 秒/遍），
三倍时间还在中间留了个"半新半旧"的包 —— 一旦那一版被部署出去，症状会随机得查不动
（2026-10-05 现场：/artlaunch 与 /system/bin/artlaunch 是两条条目，init 起的是后者，
  只换一条 = 换了个寂寞）。

用法：python replace_initrd_entries.py <initrd.gz> <输出.gz> <包内路径>=<本地文件> [...]
      先列条目：  python replace_initrd_entries.py <initrd.gz> --list <名字关键字>
"""
import gzip
import hashlib
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from replace_initrd_entry import parse, emit  # noqa: E402  同一份解析/重建器，不写第二套


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    src = sys.argv[1]
    with gzip.open(src, "rb") as f:
        raw = f.read()
    entries = parse(raw)

    if sys.argv[2] == "--list":
        kw = sys.argv[3] if len(sys.argv) > 3 else ""
        for h, n, d in entries:
            if kw in n:
                print("%-46s %10dB  sha1=%s  mode=%s"
                      % (n, len(d), hashlib.sha1(d).hexdigest()[:12], h[14:22].decode()))
        print("条目合计 %d" % len(entries))
        return

    dst = sys.argv[2]
    plan = {}
    for spec in sys.argv[3:]:
        name, path = spec.split("=", 1)
        plan[name.lstrip("./")] = open(path, "rb").read()

    done = set()
    out = []
    for hdr, name, data in entries:
        key = name.lstrip("./")
        if key in plan:
            print("替换 %s：%dB → %dB" % (name, len(data), len(plan[key])))
            out.append((hdr, name, plan[key]))
            done.add(key)
        else:
            out.append((hdr, name, data))
    missing = set(plan) - done
    if missing:
        # 必须失败退出且不写输出：静默少换一个条目 == 部署一个"看着换过了其实没换"的包
        print("包里查无这些条目：%s（一个字节都没写）" % ", ".join(sorted(missing)))
        sys.exit(2)
    rebuilt = emit(out)
    with gzip.GzipFile(dst, "wb", mtime=0) as g:
        g.write(rebuilt)
    print("写出 %s（解压后 %dB，原 %dB，条目 %d）" % (dst, len(rebuilt), len(raw), len(out)))


if __name__ == "__main__":
    main()
