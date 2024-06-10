use std::path::Path;
use winresource::WindowsResource;

fn main() {
    let icon_path = "resources/min-ed-launcher.ico";
    
    if Path::new(icon_path).is_file() {
        let mut res = WindowsResource::new();
        
        res.set_icon(icon_path);
        res.compile().expect("failed to build executable icon");
    }
}
