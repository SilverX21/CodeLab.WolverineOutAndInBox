var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.CodeLab_WolverineOutAndInBox_Api>("codelab-wolverineoutandinbox-api");

builder.Build().Run();
