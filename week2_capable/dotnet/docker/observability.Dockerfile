# Build context: repo root (kept consistent with agent.Dockerfile even though this
# image only needs week2_capable/dotnet -- one context, two Dockerfiles, simpler compose file).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY week2_capable/dotnet/Boukensha.slnx ./week2_capable/dotnet/
COPY week2_capable/dotnet/src ./week2_capable/dotnet/src
WORKDIR /src/week2_capable/dotnet
RUN dotnet publish src/Boukensha.Observability/Boukensha.Observability.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Boukensha.Observability.dll"]
