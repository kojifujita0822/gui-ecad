#r "bin/Debug/net8.0/GuiEcad.Core.dll"

using GuiEcad.Model;
using GuiEcad.Persistence;

var doc = new LadderDocument();
doc.Info.Title = "Test Document";

var sheet1 = new Sheet { 
    PageNumber = 1, 
    Name = "Sheet 1",
    Grid = new GridSpec { Rows = 8, Columns = 8 }
};
sheet1.Elements.Add(new ElementInstance {
    Kind = ElementKind.ContactNO,
    Pos = new GridPos(0, 0),
    DeviceName = "SW1"
});

var sheet2 = new Sheet { 
    PageNumber = 2, 
    Name = "Sheet 2",
    Grid = new GridSpec { Rows = 8, Columns = 8 }
};
sheet2.Elements.Add(new ElementInstance {
    Kind = ElementKind.Coil,
    Pos = new GridPos(0, 7),
    DeviceName = "CR1"
});

doc.Sheets.Add(sheet1);
doc.Sheets.Add(sheet2);

var json = GcadSerializer.Serialize(doc);
System.Console.WriteLine(json);
