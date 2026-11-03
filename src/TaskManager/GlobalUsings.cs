// WPF projects leave System.IO out of implicit usings (Path clashes with System.Windows.Shapes.Path).
// Code files here never import System.Windows.Shapes, so it is safe to bring it back.
global using System.IO;
global using System.Net.Http;
