"""Render catalog-based QHY600 frames for N.I.N.A. directory-camera playback.

Requires numpy, astropy, requests and Pillow. Generated files stay outside Git.
"""

import argparse
import csv
import json
import math
from pathlib import Path
import struct
import xml.etree.ElementTree as ET

import erfa
import numpy as np
from astropy.coordinates import SkyCoord
from astropy.io import fits
from astropy.time import Time, TimeDelta
import astropy.units as u
from PIL import Image
import requests


def unit(vector):
    return vector / np.linalg.norm(vector)


def rotation(axis, angle):
    x, y, z = unit(np.asarray(axis, dtype=float))
    cross = np.array([[0, -z, y], [z, 0, -x], [-y, x, 0]])
    return np.eye(3) + math.sin(angle) * cross + (1 - math.cos(angle)) * (cross @ cross)


def catalog(cache):
    cache.mkdir(parents=True, exist_ok=True)
    gaia = cache / "gaia-north-pole.csv"
    if not gaia.exists():
        query = ("SELECT TOP 30000 source_id,ra,dec,phot_g_mean_mag,pmra,pmdec "
                 "FROM gaiadr3.gaia_source WHERE dec>86.5 AND phot_g_mean_mag<15 "
                 "ORDER BY phot_g_mean_mag")
        response = requests.get("https://gea.esac.esa.int/tap-server/tap/sync", params={
            "REQUEST": "doQuery", "LANG": "ADQL", "FORMAT": "csv", "QUERY": query}, timeout=180)
        response.raise_for_status()
        gaia.write_text(response.text, encoding="utf-8")
    rows = list(csv.DictReader(gaia.open(encoding="utf-8")))
    stars = [(float(r["ra"]), float(r["dec"]), float(r["phot_g_mean_mag"]),
              float(r["pmra"] or 0), float(r["pmdec"] or 0), 2016.) for r in rows]
    hip = cache / "hipparcos-bright.tsv"
    if not hip.exists():
        response = requests.get("https://vizier.cds.unistra.fr/viz-bin/asu-tsv", params={
            "-source": "I/239/hip_main", "-out": "HIP,RAICRS,DEICRS,Vmag,pmRA,pmDE",
            "DEICRS": ">86.5", "Vmag": "<6", "-out.max": "100"}, timeout=90)
        response.raise_for_status()
        hip.write_text(response.text, encoding="utf-8")
    # Add only missing bright sources; Gaia is incomplete at Polaris's brightness.
    for line in hip.read_text(encoding="utf-8").splitlines():
        fields = line.split("\t")
        if len(fields) != 6 or not fields[0].strip().isdigit():
            continue
        _, ra, dec, mag, pmra, pmdec = map(float, fields)
        separation = SkyCoord(ra=ra*u.deg, dec=dec*u.deg).separation(
            SkyCoord(ra=np.array(stars)[:, 0]*u.deg, dec=np.array(stars)[:, 1]*u.deg))
        if np.min(separation.arcsec) > 20:
            stars.append((ra, dec, mag, pmra, pmdec, 1991.25))
    return stars


def icrs_basis(horizon, time, latitude, longitude):
    def convert(vector):
        vector = unit(vector)
        ra, dec = erfa.atoc13("A", math.atan2(vector[0], vector[1]),
            math.acos(np.clip(vector[2], -1, 1)), time.jd1, time.jd2, 0,
            math.radians(longitude), math.radians(latitude), 0, 0, 0, 0, 0, 0, 0)
        return np.array([math.cos(dec)*math.cos(ra), math.cos(dec)*math.sin(ra), math.sin(dec)])
    center = convert(horizon[:, 2])
    up = convert(horizon[:, 2] + horizon[:, 1]*1e-4)
    up = unit(up - center*np.dot(center, up))
    right = unit(np.cross(up, center))
    return np.column_stack((right, up, center))


def write_xisf(path, pixels, timestamp, scale, focal_length, latitude, longitude):
    """Uncompressed UInt16 XISF; N.I.N.A. preserves DATE-AVG from this format."""
    ns = "http://www.pixinsight.com/xisf"
    ET.register_namespace("", ns)
    root = ET.Element(f"{{{ns}}}xisf", version="1.0")
    image = ET.SubElement(root, f"{{{ns}}}Image", geometry=f"{pixels.shape[1]}:{pixels.shape[0]}:1",
        sampleFormat="UInt16", colorSpace="Gray", imageType="LIGHT", byteOrder="little",
        location=f"attachment:4096:{pixels.size*2}")
    for name, kind, value in [
        ("Observation:Time:Start", "TimePoint", timestamp),
        ("Instrument:ExposureTime", "Float64", "1"),
        ("Instrument:Camera:Name", "String", "QHY600 catalog simulation"),
        ("Observation:Location:Latitude", "Float64", str(latitude)),
        ("Observation:Location:Longitude", "Float64", str(longitude)),
    ]:
        prop = ET.SubElement(image, f"{{{ns}}}Property", id=name, type=kind)
        if kind == "String":
            prop.text = value
        else:
            prop.set("value", value)
    for name, value in {"DATE-AVG": f"'{timestamp}'", "XPIXSZ": str(scale),
                        "YPIXSZ": str(scale), "FOCALLEN": str(focal_length),
                        "XBINNING": "1", "YBINNING": "1", "SYNTHET": "T"}.items():
        ET.SubElement(image, f"{{{ns}}}FITSKeyword", name=name, value=value, comment="Synthetic replay")
    xml = ET.tostring(root, encoding="utf-8", xml_declaration=True)
    if len(xml) + 16 > 4096:
        raise ValueError("XISF header exceeds reserved block")
    with path.open("wb") as stream:
        stream.write(b"XISF0100" + struct.pack("<II", len(xml), 0) + xml)
        stream.write(b"\0"*(4096 - stream.tell()))
        pixels.astype("<u2", copy=False).tofile(stream)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--latitude", type=float, default=37.5)
    parser.add_argument("--longitude", type=float, default=127.)
    parser.add_argument("--bin", type=int, choices=(1, 2, 4), default=1)
    args = parser.parse_args()
    for folder in ("camera", "fits", "previews"):
        (args.output/folder).mkdir(parents=True, exist_ok=True)
    if any((args.output/"camera").iterdir()):
        raise RuntimeError("Use a fresh output directory so old playback frames cannot be mixed in")
    stars = np.asarray(catalog(args.cache))
    start = Time("2026-10-20T12:00:00", scale="utc")
    years = start.jyear - stars[:, 5]
    # First-order proper-motion propagation is adequate for these synthetic PSFs.
    ra = np.radians(stars[:, 0] + stars[:, 3]*years/(3.6e6*np.cos(np.radians(stars[:, 1]))))
    dec = np.radians(stars[:, 1] + stars[:, 4]*years/3.6e6)
    star_vectors = np.column_stack((np.cos(dec)*np.cos(ra), np.cos(dec)*np.sin(ra), np.sin(dec)))
    width, height = 9576//args.bin, 6388//args.bin
    pixel_um, focal_mm = 3.76*args.bin, 1000.
    focal_pixels = focal_mm/(pixel_um/1000)
    scale = 206264.80624709636/focal_pixels
    altitude_error, azimuth_error = math.radians(10/60), math.radians(12/60)
    lat = math.radians(args.latitude)
    axis = np.array([math.cos(lat+altitude_error)*math.sin(azimuth_error),
        math.cos(lat+altitude_error)*math.cos(azimuth_error), math.sin(lat+altitude_error)])
    center = unit(axis + np.array([1., 0., 0.])*math.radians(.1))
    up = unit(np.array([0., 0., 1.]) - center*center[2])
    first = np.column_stack((np.cross(up, center), up, center))
    second = rotation(axis, math.pi/2) @ first
    steps = [("01_reference", first, axis, 0.), ("02_ra_90deg", second, axis, 0.)]
    for i, fraction in enumerate((0., .25, .5, .75, 1.), start=3):
        correction = rotation([1, 0, 0], -altitude_error*fraction) @ rotation([0, 0, 1], azimuth_error*fraction)
        steps.append((f"{i:02d}_adjust_{round(fraction*100):03d}", correction @ second,
                      correction @ axis, fraction))
    manifest = {"synthetic": True, "catalog": "ESA Gaia DR3 G<15 plus missing Hipparcos V<6 stars",
        "latitude": args.latitude, "longitude": args.longitude, "width": width, "height": height,
        "pixelSizeUm": pixel_um, "focalLengthMm": focal_mm, "focalRatio": 5,
        "nativeBinning": args.bin, "pixelScaleArcseconds": scale,
        "fieldWidthDegrees": math.degrees(2*math.atan(width/(2*focal_pixels))),
        "fieldHeightDegrees": math.degrees(2*math.atan(height/(2*focal_pixels))), "frames": []}
    for index, (name, horizon, current_axis, fraction) in enumerate(steps):
        time = start + TimeDelta(index*10, format="sec")
        basis = icrs_basis(horizon, time, args.latitude, args.longitude)
        projected = star_vectors @ basis
        x = width/2 + focal_pixels*projected[:, 0]/projected[:, 2]
        y = height/2 - focal_pixels*projected[:, 1]/projected[:, 2]
        selected = np.flatnonzero((projected[:, 2] > 0) & (x >= 12) & (x < width-12) & (y >= 12) & (y < height-12))
        rng = np.random.default_rng(600 + index)
        pixels = rng.normal(1000, 7, (height, width)).astype(np.float32)
        sigma = max(.85, 1.6/args.bin)
        radius = max(5, math.ceil(5*sigma))
        for star in selected:
            cx, cy = x[star], y[star]
            ix, iy = int(cx), int(cy)
            xx = np.arange(ix-radius, ix+radius+1) - cx
            yy = np.arange(iy-radius, iy+radius+1) - cy
            amplitude = 150*10**(-.4*(stars[star, 2]-15))*args.bin**2
            pixels[iy-radius:iy+radius+1, ix-radius:ix+radius+1] += amplitude*np.exp(
                -(yy[:, None]**2 + xx[None, :]**2)/(2*sigma*sigma))
        pixels = np.clip(pixels, 0, 65535).astype(np.uint16)
        timestamp = time.utc.isot + "Z"
        ra_center = math.degrees(math.atan2(basis[1, 2], basis[0, 2])) % 360
        dec_center = math.degrees(math.asin(basis[2, 2]))
        header = fits.Header({"INSTRUME": "QHY600 catalog simulation", "SYNTHET": True,
            "XPIXSZ": pixel_um, "YPIXSZ": pixel_um, "FOCALLEN": focal_mm, "FOCRATIO": 5,
            "EXPTIME": 1., "DATE-OBS": timestamp, "DATE-AVG": timestamp,
            "RA": ra_center, "DEC": dec_center, "SITELAT": args.latitude, "SITELONG": args.longitude,
            "IMAGETYP": "LIGHT", "XBINNING": 1, "YBINNING": 1})
        fits.PrimaryHDU(pixels, header=header).writeto(args.output/"fits"/f"{name}.fits")
        write_xisf(args.output/"camera"/f"{name}.xisf", pixels, timestamp, pixel_um, focal_mm,
                   args.latitude, args.longitude)
        preview = np.arcsinh(np.maximum(pixels.astype(np.float32)-980, 0)/35)/np.arcsinh(3000/35)
        preview = Image.fromarray(np.uint8(np.clip(preview, 0, 1)*255))
        preview.thumbnail((1600, 1100))
        preview.save(args.output/"previews"/f"{name}.png")
        pole = np.array([0, math.cos(lat), math.sin(lat)])
        total = math.degrees(math.atan2(np.linalg.norm(np.cross(current_axis, pole)), current_axis @ pole))*60
        expected_alt = math.degrees(math.asin(current_axis[2])-lat)*60
        expected_az = math.degrees(math.atan2(current_axis[0], current_axis[1]))*60
        manifest["frames"].append({"name": name, "capturedAtUtc": timestamp, "stars": len(selected),
            "raDegrees": ra_center, "decDegrees": dec_center, "correctionFraction": fraction,
            "expectedAltitudeArcminutes": expected_alt, "expectedAzimuthArcminutes": expected_az,
            "expectedTotalArcminutes": total})
        print(f"{name}: {width}x{height}, {len(selected)} catalog stars, expected error {total:.3f} arcmin", flush=True)
    (args.output/"manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"Simulator directory: {args.output/'camera'}", flush=True)


if __name__ == "__main__":
    main()
