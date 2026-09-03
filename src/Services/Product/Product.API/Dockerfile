FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5004

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Services/Product/Product.API/Product.API.csproj", "Services/Product/Product.API/"]
COPY ["Services/Product/Product.Domain/Product.Domain.csproj", "Services/Product/Product.Domain/"]
COPY ["Services/Product/Product.Infrastructure/Product.Infrastructure.csproj", "Services/Product/Product.Infrastructure/"]
COPY ["Shared/Shared.Contracts/Shared.Contracts.csproj", "Shared/Shared.Contracts/"]
COPY ["Shared/Shared.Infrastructure/Shared.Infrastructure.csproj", "Shared/Shared.Infrastructure/"]
RUN dotnet restore "Services/Product/Product.API/Product.API.csproj"
COPY . .
WORKDIR "/src/Services/Product/Product.API"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:5004
ENTRYPOINT ["dotnet", "Product.API.dll"]
