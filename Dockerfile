FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["ProgramCSDemo.csproj", "./"]
RUN dotnet restore "ProgramCSDemo.csproj"
COPY . .
RUN dotnet publish "ProgramCSDemo.csproj" --configuration Release --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
RUN mkdir -p /data && chown app:app /data
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
ENV URL_SHORTENER_DATA_PATH=/data/links.json
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ProgramCSDemo.dll"]
