#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""替换 cpio.gz(newc) initrd 里**已存在**条目的内容（保持名字/mode，重写 size 与填充）。

为什么不用 append_initrd.py：那个只在 TRAILER 前追加，靠"同名后者覆盖"生效；
initramfs 解包器确实以后者为准，但那样会在包里留下两份 init，排查时看不出哪份在跑。
这里整包重建，包里永远只有一份。

用法：python replace_initrd_entry.py <initrd.gz> <包内路径> <本地文件> [输出.gz]
"""
import gzip
import sys


def parse(blob):
    """→ [(header:bytes, name:str, data:bytes)]，header 是原始 110 字节头。"""
    i, out = 0, []
    while i + 110 <= len(blob):
        if blob[i:i + 6] != b"070701":
            break
        hdr = blob[i:i + 110]
        f = lambda off, w=8: int(hdr[off:off + w], 16)
        nsize, size = f(94), f(54)
        name = blob[i + 110:i + 110 + nsize - 1].decode("utf-8", "replace")
        d = (i + 110 + nsize + 3) // 4 * 4
        data = blob[d:d + size]
        out.append((hdr, name, data))
        i = (d + size + 3) // 4 * 4
        if name == "TRAILER!!!":
            break
    return out


def emit(entries):
    """整包重建：头 110 字节 + name(含 NUL) 补到 4 + data 补到 4。"""
    buf = bytearray()
    for hdr, name, data in entries:
        h = bytearray(hdr)
        n = name.encode()
        h[54:62] = ("%08X" % len(data)).encode()      # filesize
        h[94:102] = ("%08X" % (len(n) + 1)).encode()  # namesize **含结尾 NUL**：漏 +1 会让解包器把每个名字截掉最后一个字符
        buf += h
        buf += n
        buf += b"\0"
        while len(buf) % 4:
            buf += b"\0"
        buf += data
        while len(buf) % 4:
            buf += b"\0"
    return bytes(buf)


def main():
    src, want, local = sys.argv[1], sys.argv[2], sys.argv[3]
    dst = sys.argv[4] if len(sys.argv) > 4 else src
    raw = gzip.decompress(open(src, "rb").read())
    entries = parse(raw)
    new = open(local, "rb").read()
    hit = [e for e in entries if e[1].lstrip("./") == want.lstrip("./")]
    if not hit:
        print("包里找不到条目 %s（现有条目样例：%s）" % (want, [e[1] for e in entries[:6]]))
        sys.exit(2)
    out = []
    for hdr, name, data in entries:
        if name.lstrip("./") == want.lstrip("./"):
            print("替换 %s：%dB → %dB" % (name, len(data), len(new)))
            out.append((hdr, name, new))
        else:
            out.append((hdr, name, data))
    rebuilt = emit(out)
    with gzip.GzipFile(dst, "wb", mtime=0) as g:
        g.write(rebuilt)
    print("写出 %s（解压后 %dB，原 %dB，条目 %d）" % (dst, len(rebuilt), len(raw), len(out)))


if __name__ == "__main__":
    main()
