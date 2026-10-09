// Usage: bench_cgal <expanded.json> <out-dir>
// CGAL with the exact-predicates-exact-constructions kernel (Epeck), the closest match to NanoCut's exact arithmetic:
// workpiece box minus the convex hull of each step's points, in order (corefinement on a Surface_mesh).
// Only the cutting is timed; states listed in "save" are written as binary STL.
#include <CGAL/Exact_predicates_exact_constructions_kernel.h>
#include <CGAL/Surface_mesh.h>
#include <CGAL/convex_hull_3.h>
#include <CGAL/Polygon_mesh_processing/corefinement.h>
#include <CGAL/Polygon_mesh_processing/measure.h>
#include <CGAL/Polygon_mesh_processing/triangulate_faces.h>
#include <nlohmann/json.hpp>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <fstream>
#include <iostream>
#include <set>
#include <vector>

using K = CGAL::Exact_predicates_exact_constructions_kernel;
using Point = K::Point_3;
using Mesh = CGAL::Surface_mesh<Point>;
namespace PMP = CGAL::Polygon_mesh_processing;
using json = nlohmann::json;
using Clock = std::chrono::steady_clock;

static Mesh hull(const std::vector<Point>& pts) {
  Mesh m;
  CGAL::convex_hull_3(pts.begin(), pts.end(), m);
  PMP::triangulate_faces(m);
  return m;
}

static void write_stl(const Mesh& m, const std::string& path) {
  std::ofstream f(path, std::ios::binary);
  char header[80] = {};
  f.write(header, 80);
  uint32_t n = static_cast<uint32_t>(m.number_of_faces());
  f.write(reinterpret_cast<char*>(&n), 4);
  for (auto face : m.faces()) {
    float zero[3] = {0, 0, 0};
    f.write(reinterpret_cast<char*>(zero), 12);
    for (auto v : CGAL::vertices_around_face(m.halfedge(face), m)) {
      const Point& p = m.point(v);
      float c[3] = {float(CGAL::to_double(p.x()) * 1e-6), float(CGAL::to_double(p.y()) * 1e-6),
                    float(CGAL::to_double(p.z()) * 1e-6)};
      f.write(reinterpret_cast<char*>(c), 12);
    }
    uint16_t attr = 0;
    f.write(reinterpret_cast<char*>(&attr), 2);
  }
}

int main(int argc, char** argv) {
  if (argc < 3) { std::cerr << "usage: bench_cgal <expanded.json> <out-dir>\n"; return 2; }
  json scene = json::parse(std::ifstream(argv[1]));
  std::string out = argv[2];
  std::set<int> save;
  for (auto& s : scene["save"]) save.insert(s.get<int>());

  auto mn = scene["box"]["min"], mx = scene["box"]["max"];
  std::vector<Point> corners;
  for (int i = 0; i < 8; i++)
    corners.emplace_back((i & 1 ? mx : mn)[0].get<double>(), (i & 2 ? mx : mn)[1].get<double>(),
                         (i & 4 ? mx : mn)[2].get<double>());
  Mesh work = hull(corners);
  if (save.count(0)) write_stl(work, out + "/step-0000.stl");

  std::vector<double> stepMs;
  double totalMs = 0;
  int count = static_cast<int>(scene["steps"].size());
  for (int i = 0; i < count; i++) {
    std::vector<Point> pts;
    for (auto& p : scene["steps"][i]) pts.emplace_back(p[0].get<double>(), p[1].get<double>(), p[2].get<double>());
    auto t0 = Clock::now();
    Mesh tool = hull(pts);
    Mesh result;
    if (!PMP::corefine_and_compute_difference(work, tool, result)) { std::cerr << "step " << i << ": difference failed\n"; return 1; }
    work = std::move(result);
    double ms = std::chrono::duration<double, std::milli>(Clock::now() - t0).count();
    stepMs.push_back(ms);
    totalMs += ms;
    if (save.count(i + 1) || (i == count - 1 && save.count(-1))) {
      char name[32];
      std::snprintf(name, sizeof name, "/step-%04d.stl", i + 1);
      write_stl(work, out + name);
    }
  }
  double volume = CGAL::to_double(PMP::volume(work)) * 1e-18;
  json stats = {{"engine", "cgal"}, {"language", "C++"}, {"exact", true}, {"steps", count}, {"totalMs", totalMs},
                {"stepMs", stepMs}, {"volumeMm3", volume}, {"triangles", work.number_of_faces()}};
  std::ofstream(out + "/stats.json") << stats.dump(2);
  std::printf("cgal: %d steps in %.0f ms, V = %.9f mm3, %zu triangles\n", count, totalMs, volume, (size_t)work.number_of_faces());
  return 0;
}
