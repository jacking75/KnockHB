# 시그널링 서버 컨테이너 이미지.
# Go의 단일 정적 바이너리 이점을 그대로 살려 distroless로 마무리한다.

FROM golang:1.27 AS build
WORKDIR /src
COPY server/signaling/ ./
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/signaling .

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/signaling /signaling
EXPOSE 25001
USER nonroot:nonroot
ENTRYPOINT ["/signaling"]
