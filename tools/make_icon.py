# -*- coding: utf-8 -*-
"""生成应用图标 src/app.ico（与程序内自绘托盘图标同款设计：深色圆角方块 + 青蓝表圈 + 白色指针 + 红色轴心）。
纯标准库实现：SDF 超采样抗锯齿，16/24/32/48 以 BMP 编码、256 以 PNG 编码写入 ICO。
用法: python tools/make_icon.py  （在仓库根目录执行，输出 src/app.ico）"""

import math, os, struct, zlib

SIZES = [16, 24, 32, 48, 256]

def clamp(v, lo, hi):
    return lo if v < lo else (hi if v > hi else v)

def sd_rounded_rect(px, py, cx, cy, hw, hh, r):
    """圆角矩形 SDF（负=内部）"""
    dx = abs(px - cx) - (hw - r)
    dy = abs(py - cy) - (hh - r)
    ax, ay = max(dx, 0.0), max(dy, 0.0)
    return math.hypot(ax, ay) + min(max(dx, dy), 0.0) - r

def sd_segment(px, py, x1, y1, x2, y2, r):
    """胶囊 SDF（负=内部）"""
    vx, vy = x2 - x1, y2 - y1
    wx, wy = px - x1, py - y1
    L2 = vx * vx + vy * vy
    t = 0.0 if L2 == 0 else clamp((wx * vx + wy * vy) / L2, 0.0, 1.0)
    bx, by = wx - t * vx, wy - t * vy
    return math.hypot(bx, by) - r

def sd_circle(px, py, cx, cy, r):
    return math.hypot(px - cx, py - cy) - r

def blend(dst, src, a):
    return tuple(round(d + (s - d) * a) for d, s in zip(dst, src))

BG      = (0x1B, 0x1B, 0x1F)
BORDER  = (0x3A, 0x3A, 0x41)
ACCENT  = (0x60, 0xCD, 0xFF)
WHITE   = (0xFF, 0xFF, 0xFF)
RED     = (0xDC, 0x26, 0x26)

def render_rgba(size):
    """渲染 size x size 的 BGRA 像素（顶部起始行序），2x 超采样"""
    ss = 2
    n = size * ss
    buf = bytearray(size * size * 4)
    c = size / 2.0
    corner_r = size * 0.22          # 圆角方块
    border_w = max(1.0, size * 0.035)
    ring_r = size * 0.30            # 表圈半径
    ring_w = max(1.5, size * 0.075)
    hand_w = max(1.0, size * 0.065)
    dot_r = max(1.0, size * 0.055)
    for y in range(size):
        for x in range(size):
            r = g = b = a = 0.0
            for sy in range(ss):
                for sx in range(ss):
                    px = (x * ss + sx + 0.5) / ss - 0.5 + 0.5   # 像素中心
                    py = (y * ss + sy + 0.5) / ss - 0.5 + 0.5
                    px = x + (sx + 0.5) / ss
                    py = y + (sy + 0.5) / ss
                    fx, fy = px, py
                    # 底色圆角方块（外部 alpha=0）
                    d_bg = sd_rounded_rect(fx, fy, c, c, c - 1, c - 1, corner_r)
                    if d_bg >= 0.6:
                        continue
                    cov_bg = clamp(0.5 - d_bg, 0.0, 1.0)
                    col, al = BG, cov_bg
                    # 描边
                    if abs(d_bg) < border_w / 2 + 0.5:
                        b_a = clamp(border_w / 2 + 0.5 - abs(d_bg), 0.0, 1.0) * cov_bg
                        col, al = blend(col, BORDER, b_a), max(al, b_a if False else al * (1 - b_a) + b_a)
                    # 表圈
                    d_ring = abs(sd_circle(fx, fy, c, c, ring_r)) - ring_w / 2
                    if d_ring < 0.5:
                        r_a = clamp(0.5 - d_ring, 0.0, 1.0) * cov_bg
                        col, al = blend(col, ACCENT, r_a), min(1.0, al + r_a * (1 - al))
                    # 指针（上 + 右）
                    d_h1 = sd_segment(fx, fy, c, c, c, c - size * 0.20, hand_w / 2)
                    d_h2 = sd_segment(fx, fy, c, c, c + size * 0.15, c, hand_w / 2)
                    d_hand = min(d_h1, d_h2)
                    if d_hand < 0.5:
                        h_a = clamp(0.5 - d_hand, 0.0, 1.0) * cov_bg
                        col, al = blend(col, WHITE, h_a), min(1.0, al + h_a * (1 - al))
                    # 轴心
                    d_dot = sd_circle(fx, fy, c, c, dot_r)
                    if d_dot < 0.5:
                        d_a = clamp(0.5 - d_dot, 0.0, 1.0) * cov_bg
                        col, al = blend(col, RED, d_a), min(1.0, al + d_a * (1 - al))
                    i = (y * size + x) * 4
                    # 累加（同像素多个子样直接覆盖为最后一个的合成结果即可，此处用均值近似）
                    buf[i]     = (buf[i] + round(col[0] * al)) // 2 if a > 0 else round(col[0] * al)
                    buf[i + 1] = (buf[i+1] + round(col[1] * al)) // 2 if a > 0 else round(col[1] * al)
                    buf[i + 2] = (buf[i+2] + round(col[2] * al)) // 2 if a > 0 else round(col[2] * al)
                    buf[i + 3] = (buf[i+3] + round(al * 255)) // 2 if a > 0 else round(al * 255)
                    a = 1
    return bytes(buf)

def png_chunk(tag, data):
    c = struct.pack('>I', len(data)) + tag + data
    return c + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

def encode_png(size, rgba):
    raw = b''.join(b'\x00' + rgba[y * size * 4:(y + 1) * size * 4] for y in range(size))
    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + png_chunk(b'IHDR', ihdr)
            + png_chunk(b'IDAT', zlib.compress(raw, 9)) + png_chunk(b'IEND', b''))

def encode_bmp(size, rgba):
    """ICO 内嵌 BMP：BITMAPINFOHEADER(高度x2) + 自下而上 BGRA + 全零 AND 掩码"""
    hdr = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    xor = bytearray(size * size * 4)
    for y in range(size):
        src_row = (size - 1 - y) * size * 4
        for x in range(size):
            si = src_row + x * 4
            di = (y * size + x) * 4
            xor[di]     = rgba[si + 2]  # B
            xor[di + 1] = rgba[si + 1]  # G
            xor[di + 2] = rgba[si]      # R
            xor[di + 3] = rgba[si + 3]  # A
    stride = ((size + 31) // 32) * 4
    and_mask = bytes(stride * size)
    return hdr + bytes(xor) + and_mask

def build_ico():
    images = []
    for s in SIZES:
        rgba = render_rgba(s)
        data = encode_png(s, rgba) if s >= 256 else encode_bmp(s, rgba)
        images.append((s, data))
    out = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b''
    for s, data in images:
        w = 0 if s >= 256 else s
        entries += struct.pack('<BBBBHHII', w, w, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    ico = out + entries + b''.join(d for _, d in images)
    os.makedirs('src', exist_ok=True)
    with open(os.path.join('src', 'app.ico'), 'wb') as f:
        f.write(ico)
    print('src/app.ico written:', len(ico), 'bytes,', len(images), 'sizes')

if __name__ == '__main__':
    build_ico()
