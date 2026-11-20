# Run by SketchUp at startup (SketchUp.exe -RubyStartup export.rb model.skp): writes the open model beside this script as
#   <name>.obj     every face as triangles in metres, z up, a group per material (a face with none takes its group's or component's)
#   <name>.txt     the model's text labels with the point each is attached to (the sources and receivers), in metres
# and then closes SketchUp. Errors go to export.log.
require 'sketchup.rb'
OUT = File.dirname(__FILE__)
M = 0.0254

def walk(entities, transform, inherited, triangles, texts, parent_hidden)
  entities.each do |e|
    hidden = parent_hidden || (e.respond_to?(:hidden?) && e.hidden?) || (e.respond_to?(:layer) && e.layer && !e.layer.visible?)
    case e
    when Sketchup::Face
      material = e.material || inherited || e.back_material
      name = (material ? material.display_name : 'none') + (hidden ? '__hidden' : '')
      mesh = e.mesh(0)
      (1..mesh.count_polygons).each do |i|
        pts = mesh.polygon_points_at(i).map { |p| p.transform(transform) }
        (triangles[name] ||= []) << pts if pts.length == 3
      end
    when Sketchup::Group
      walk(e.entities, transform * e.transformation, e.material || inherited, triangles, texts, hidden)
    when Sketchup::ComponentInstance
      walk(e.definition.entities, transform * e.transformation, e.material || inherited, triangles, texts, hidden)
    when Sketchup::Text
      p = e.point ? e.point.transform(transform) : nil
      texts << [e.text.gsub(/\s+/, ' '), p]
    end
  end
end

def export_active
  begin
    model = Sketchup.active_model
    name = File.basename(model.path, '.skp')
    triangles = {}
    texts = []
    walk(model.entities, Geom::Transformation.new, nil, triangles, texts, false)
    File.open(File.join(OUT, name + '.obj'), 'w') do |f|
      n = 0
      triangles.keys.sort.each do |material|
        f.puts "g #{material.gsub(/\s+/, '_')}"
        f.puts "usemtl #{material.gsub(/\s+/, '_')}"
        triangles[material].each do |t|
          t.each { |p| f.puts format('v %.5f %.5f %.5f', p.x.to_f * M, p.y.to_f * M, p.z.to_f * M) }
          f.puts "f #{n + 1} #{n + 2} #{n + 3}"
          n += 3
        end
      end
    end
    File.open(File.join(OUT, name + '.txt'), 'w') do |f|
      f.puts "model #{model.path}"
      texts.each { |t, p| f.puts(p ? format('%.4f %.4f %.4f | %s', p.x.to_f * M, p.y.to_f * M, p.z.to_f * M, t) : "- | #{t}") }
      f.puts 'materials: ' + model.materials.map(&:display_name).join(', ')
      f.puts 'layers: ' + model.layers.map { |l| "#{l.name}#{l.visible? ? '' : ' (hidden)'}" }.join(', ')
    end
    File.open(File.join(OUT, 'export.log'), 'a') { |f| f.puts "#{name}: #{triangles.values.map(&:length).inject(0, :+)} triangles, #{texts.length} labels" }
  rescue Exception => ex
    File.open(File.join(OUT, 'export.log'), 'a') { |f| f.puts "ERROR #{ex.class}: #{ex.message}\n#{ex.backtrace.first(5).join("\n")}" }
  end
end

UI.start_timer(3, false) do
  export_active
  other = File.join(File.dirname(Sketchup.active_model.path), 'CR2.skp')
  Sketchup.open_file(other) if File.exist?(other)
  UI.start_timer(3, false) do
    export_active
    Sketchup.quit
  end
end
