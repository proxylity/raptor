# The MicroVM image: one NativeAOT executable on Lambda's minimal, snapshot-compatible base.
# microvm/scripts/publish.sh stages this file and the executable in microvm/build/image/.
FROM public.ecr.aws/lambda/microvms:al2023-minimal
COPY raptor2-microvm /app/raptor2-microvm
# 8080 receives packet batches from Proxylity; 9000 receives Lambda lifecycle hooks.
EXPOSE 8080 9000
ENTRYPOINT ["/app/raptor2-microvm"]
